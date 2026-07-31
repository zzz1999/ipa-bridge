using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace IPABridge.Infrastructure;

/// <summary>
/// Runs a console application inside Windows ConPTY. This allows command-line tools
/// that require real terminal semantics to receive secrets over the terminal instead
/// of exposing those secrets in the process command line.
/// </summary>
public sealed class ConPtyProcessRunner
{
    public async Task<ConPtyResult> RunAsync(
        string executablePath,
        IEnumerable<string> arguments,
        IReadOnlyList<ConPtyPrompt>? prompts = null,
        Action<string>? outputReceived = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            throw new PlatformNotSupportedException(
                "Secure terminal sign-in requires Windows 10 version 1809 or later.");
        }

        NativeMethods.CreatePipePair(out var pseudoInput, out var hostInput);
        NativeMethods.CreatePipePair(out var hostOutput, out var pseudoOutput);

        IntPtr pseudoConsole = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;

        try
        {
            var result = NativeMethods.CreatePseudoConsole(
                new NativeMethods.Coord(120, 40),
                pseudoInput.DangerousGetHandle(),
                pseudoOutput.DangerousGetHandle(),
                0,
                out pseudoConsole);
            if (result != 0)
            {
                throw new Win32Exception(result, "Could not create the Windows secure terminal.");
            }

            nuint attributeListSize = 0;
            _ = NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal(checked((int)attributeListSize));
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (!NativeMethods.UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (IntPtr)NativeMethods.ProcThreadAttributePseudoConsole,
                    pseudoConsole,
                    (IntPtr)IntPtr.Size,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var startupInfo = new NativeMethods.StartupInfoEx
            {
                StartupInfo = new NativeMethods.StartupInfo
                {
                    Cb = Marshal.SizeOf<NativeMethods.StartupInfoEx>(),
                    Flags = NativeMethods.StartfUseStdHandles,
                    StandardInput = IntPtr.Zero,
                    StandardOutput = IntPtr.Zero,
                    StandardError = IntPtr.Zero
                },
                AttributeList = attributeList
            };

            var commandLine = new StringBuilder(BuildCommandLine(executablePath, arguments));
            var currentDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
            if (!NativeMethods.CreateProcess(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    NativeMethods.ExtendedStartupInfoPresent | NativeMethods.CreateUnicodeEnvironment,
                    IntPtr.Zero,
                    currentDirectory,
                    ref startupInfo,
                    out var processInformation))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not start {Path.GetFileName(executablePath)}.");
            }

            // The ConPTY communication handles must remain valid until the hosted
            // process has attached to the pseudoconsole.
            pseudoInput.Dispose();
            pseudoOutput.Dispose();

            processHandle = processInformation.Process;
            threadHandle = processInformation.Thread;
            NativeMethods.CloseHandle(threadHandle);
            threadHandle = IntPtr.Zero;

            // CreatePipe returns synchronous handles. FileStream still exposes async APIs for
            // these handles by scheduling the blocking operation on the thread pool.
            await using var inputStream = new FileStream(hostInput, FileAccess.Write, 4096, isAsync: false);
            await using var outputStream = new FileStream(hostOutput, FileAccess.Read, 4096, isAsync: false);
            await using var writer = new StreamWriter(inputStream, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true,
                NewLine = "\r\n"
            };
            using var reader = new StreamReader(outputStream, Encoding.UTF8, true, 4096, leaveOpen: true);

            var output = new StringBuilder();
            var rollingText = new StringBuilder();
            var answeredPrompts = new HashSet<string>(StringComparer.Ordinal);
            var containsSecretResponses = prompts?.Any(prompt =>
                !string.IsNullOrEmpty(prompt.Response)) == true;
            string? missingPromptKey = null;
            var readerBuffer = new char[1024];

            var readTask = Task.Run(async () =>
            {
                while (true)
                {
                    var read = await reader.ReadAsync(readerBuffer.AsMemory(0, readerBuffer.Length), CancellationToken.None);
                    if (read == 0)
                    {
                        break;
                    }

                    var chunk = new string(readerBuffer, 0, read);
                    output.Append(chunk);
                    rollingText.Append(chunk);
                    if (rollingText.Length > 8192)
                    {
                        rollingText.Remove(0, rollingText.Length - 8192);
                    }

                    // A secret echoed by a terminal can be split across arbitrary read
                    // boundaries. Do not stream authentication output until the complete
                    // response can be redacted as one value.
                    if (!containsSecretResponses)
                    {
                        outputReceived?.Invoke(chunk);
                    }

                    if (prompts is null)
                    {
                        continue;
                    }

                    var snapshot = rollingText.ToString();
                    foreach (var prompt in prompts)
                    {
                        if (answeredPrompts.Contains(prompt.Key) ||
                            !snapshot.Contains(prompt.Marker, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        answeredPrompts.Add(prompt.Key);
                        if (string.IsNullOrEmpty(prompt.Response))
                        {
                            missingPromptKey = prompt.Key;
                            NativeMethods.TerminateProcess(processHandle, 2);
                            return;
                        }

                        await writer.WriteLineAsync(prompt.Response);
                    }
                }
            }, CancellationToken.None);

            using var cancellationRegistration = cancellationToken.Register(() =>
            {
                if (processHandle != IntPtr.Zero)
                {
                    NativeMethods.TerminateProcess(processHandle, 3);
                }
            });

            await Task.Run(
                () => NativeMethods.WaitForSingleObject(processHandle, NativeMethods.Infinite),
                CancellationToken.None);

            NativeMethods.GetExitCodeProcess(processHandle, out var exitCode);
            NativeMethods.ClosePseudoConsole(pseudoConsole);
            pseudoConsole = IntPtr.Zero;

            await readTask.WaitAsync(TimeSpan.FromSeconds(3));
            cancellationToken.ThrowIfCancellationRequested();

            var sanitizedOutput = Sanitize(output.ToString(), prompts).Trim();
            if (containsSecretResponses && outputReceived is not null)
            {
                outputReceived(sanitizedOutput);
            }

            return new ConPtyResult(
                unchecked((int)exitCode),
                sanitizedOutput,
                missingPromptKey);
        }
        finally
        {
            pseudoInput.Dispose();
            pseudoOutput.Dispose();
            hostInput.Dispose();
            hostOutput.Dispose();

            if (threadHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(threadHandle);
            }

            if (processHandle != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(processHandle);
            }

            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }

            if (pseudoConsole != IntPtr.Zero)
            {
                NativeMethods.ClosePseudoConsole(pseudoConsole);
            }
        }
    }

    private static string BuildCommandLine(string executablePath, IEnumerable<string> arguments)
    {
        return string.Join(" ", new[] { QuoteArgument(executablePath) }.Concat(arguments.Select(QuoteArgument)));
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && !argument.Any(character => char.IsWhiteSpace(character) || character == '"'))
        {
            return argument;
        }

        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }

            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }

        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    private static string Sanitize(string value, IReadOnlyList<ConPtyPrompt>? prompts)
    {
        if (prompts is null)
        {
            return value;
        }

        foreach (var response in prompts
                     .Select(prompt => prompt.Response)
                     .Where(response => !string.IsNullOrEmpty(response))
                     .Distinct(StringComparer.Ordinal))
        {
            value = value.Replace(response!, "[REDACTED]", StringComparison.Ordinal);
        }

        return value;
    }

    private static class NativeMethods
    {
        public const nuint ProcThreadAttributePseudoConsole = 0x00020016;
        public const uint ExtendedStartupInfoPresent = 0x00080000;
        public const uint CreateUnicodeEnvironment = 0x00000400;
        public const int StartfUseStdHandles = 0x00000100;
        public const uint Infinite = 0xFFFFFFFF;

        [StructLayout(LayoutKind.Sequential)]
        public readonly struct Coord
        {
            public Coord(short x, short y)
            {
                X = x;
                Y = y;
            }

            public readonly short X;
            public readonly short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SecurityAttributes
        {
            public int Length;
            public IntPtr SecurityDescriptor;
            [MarshalAs(UnmanagedType.Bool)]
            public bool InheritHandle;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct StartupInfo
        {
            public int Cb;
            public string? Reserved;
            public string? Desktop;
            public string? Title;
            public int X;
            public int Y;
            public int XSize;
            public int YSize;
            public int XCountChars;
            public int YCountChars;
            public int FillAttribute;
            public int Flags;
            public short ShowWindow;
            public short Reserved2;
            public IntPtr Reserved2Pointer;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct StartupInfoEx
        {
            public StartupInfo StartupInfo;
            public IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public uint ProcessId;
            public uint ThreadId;
        }

        public static void CreatePipePair(out SafeFileHandle readHandle, out SafeFileHandle writeHandle)
        {
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                InheritHandle = true
            };
            if (!CreatePipe(out readHandle, out writeHandle, ref attributes, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreatePipe(
            out SafeFileHandle readPipe,
            out SafeFileHandle writePipe,
            ref SecurityAttributes pipeAttributes,
            int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int CreatePseudoConsole(
            Coord size,
            IntPtr input,
            IntPtr output,
            uint flags,
            out IntPtr pseudoConsole);

        [DllImport("kernel32.dll")]
        public static extern void ClosePseudoConsole(IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InitializeProcThreadAttributeList(
            IntPtr attributeList,
            int attributeCount,
            int flags,
            ref nuint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            IntPtr attribute,
            IntPtr value,
            IntPtr size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport("kernel32.dll")]
        public static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcess(
            string? applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string? currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

        [DllImport("kernel32.dll")]
        public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool TerminateProcess(IntPtr process, uint exitCode);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);
    }
}
