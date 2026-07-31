using System.Runtime.InteropServices;
using System.Text.Json;
using IPABridge.Infrastructure;
using IPABridge.Models;
using IPABridge.Services;

var fakeIpatoolMode = Environment.GetEnvironmentVariable("IPA_BRIDGE_SMOKE_FAKE_IPATOOL");
if (fakeIpatoolMode is "download-failure" or "download-success")
{
    var outputArgumentIndex = Array.IndexOf(args, "--output");
    if (outputArgumentIndex < 0 || outputArgumentIndex + 1 >= args.Length)
    {
        Console.WriteLine("{\"level\":\"error\",\"error\":\"missing output path\",\"success\":false}");
        return 2;
    }

    Console.Write("enter passphrase to unlock");
    var passphrase = Console.ReadLine();
    if (passphrase != "temporary-vault-secret")
    {
        Console.WriteLine("{\"level\":\"error\",\"error\":\"wrong passphrase\",\"success\":false}");
        return 3;
    }

    var outputPath = args[outputArgumentIndex + 1];
    Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
    if (fakeIpatoolMode == "download-failure")
    {
        File.WriteAllText(outputPath + ".tmp", "partial archive");
        File.WriteAllText(outputPath, "partial patched archive");
        Console.WriteLine("{\"level\":\"error\",\"error\":\"simulated download failure\",\"success\":false}");
        return 1;
    }

    File.WriteAllText(outputPath, "complete archive");
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        level = "info",
        output = outputPath,
        success = true
    }));
    return 0;
}

if (args is ["--prompt-child"])
{
    Console.Write("enter password:");
    var password = Console.ReadLine();
    Console.WriteLine(password == "temporary-secret"
        ? """{"success":true}"""
        : """{"success":false}""");
    return password == "temporary-secret" ? 0 : 1;
}

var failures = new List<string>();

void Check(bool condition, string description)
{
    if (condition)
    {
        Console.WriteLine($"PASS  {description}");
    }
    else
    {
        failures.Add(description);
        Console.WriteLine($"FAIL  {description}");
    }
}

bool Throws<TException>(Action action)
    where TException : Exception
{
    try
    {
        action();
        return false;
    }
    catch (TException)
    {
        return true;
    }
}

var commandFailure = new InvalidOperationException("expected command failure");
var commandExceptionCompletion = new TaskCompletionSource<Exception>(
    TaskCreationOptions.RunContinuationsAsynchronously);
var fireAndForgetCommand = new AsyncRelayCommand(
    async () =>
    {
        await Task.Yield();
        throw commandFailure;
    },
    exceptionHandler: exception => commandExceptionCompletion.TrySetResult(exception));
fireAndForgetCommand.Execute(null);
var capturedCommandException = await commandExceptionCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check(
    ReferenceEquals(capturedCommandException, commandFailure),
    "AsyncRelayCommand captures exceptions from ICommand.Execute");
Check(
    ReferenceEquals(fireAndForgetCommand.LastException, commandFailure),
    "AsyncRelayCommand retains the most recent exception for diagnostics");

var awaitedCommand = new AsyncRelayCommand(
    () => Task.FromException(commandFailure),
    exceptionHandler: _ => { });
await awaitedCommand.ExecuteAsync();
Check(awaitedCommand.CanExecute(null), "AsyncRelayCommand is re-enabled after a captured exception");

var deviceEnvironmentGate = new DeviceEnvironmentOperationGate();
var gateTransitions = 0;
deviceEnvironmentGate.StateChanged += (_, _) => gateTransitions++;
var deviceEnvironmentLease = deviceEnvironmentGate.TryEnter();
Check(
    deviceEnvironmentLease is not null && deviceEnvironmentGate.IsBusy,
    "device environment gate publishes its busy state");
Check(
    deviceEnvironmentGate.TryEnter() is null,
    "device environment gate rejects overlapping operations");
deviceEnvironmentLease?.Dispose();
deviceEnvironmentLease?.Dispose();
Check(
    !deviceEnvironmentGate.IsBusy && gateTransitions == 2,
    "device environment gate releases once and publishes its available state");

const string searchOutput =
    """
    {"level":"info","count":2,"apps":[{"id":123,"bundleID":"com.example.first","name":"First","version":"1.2.3","price":0},{"id":456,"bundleID":"com.example.second","name":"Second","version":"4.5.6","price":1.99}]}
    """;
var apps = IpatoolJsonParser.ParseSearchResults(searchOutput);
Check(apps.Count == 2, "ipatool JSON Lines search count");
Check(apps[0].BundleIdentifier == "com.example.first", "ipatool bundle identifier contract");
Check(apps[1].Price == 1.99, "ipatool numeric price contract");
Check(
    IpatoolJsonParser.ParseSearchResults("""{"level":"info","count":0,"apps":[]}""").Count == 0,
    "ipatool valid empty search contract");
Check(
    Throws<InvalidDataException>(() => IpatoolJsonParser.ParseSearchResults("""{"level":"info"}""")),
    "ipatool missing search contract is rejected");

const string downloadOutput =
    """{"level":"info","output":"D:\\Downloads\\Example.ipa","purchased":true,"success":true}""";
Check(
    IpatoolJsonParser.FindDownloadedPath(downloadOutput) == @"D:\Downloads\Example.ipa",
    "ipatool download output contract");

const string errorOutput =
    """{"level":"error","error":"license is required","success":false}""";
Check(IpatoolJsonParser.FindError(errorOutput) == "license is required", "ipatool error contract");

const string versionsOutput =
    """{"level":"info","externalVersionIdentifiers":["630253062","630253063"],"bundleID":"com.example.first","success":true}""";
Check(
    IpatoolJsonParser.ParseVersionIdentifiers(versionsOutput).Count == 2,
    "ipatool version list contract");
Check(
    Throws<InvalidDataException>(() =>
        IpatoolJsonParser.ParseVersionIdentifiers("""{"level":"info","success":true}""")),
    "ipatool missing version contract is rejected");

const string versionMetadataOutput =
    """
    terminal text before JSON
    {"level":"info","externalVersionID":"630253062","displayVersion":"0.8.0","releaseDate":"2014-07-23T19:48:08Z","success":true}
    """;
var versionMetadata = IpatoolJsonParser.ParseVersionMetadata(
    versionMetadataOutput,
    "630253062");
Check(versionMetadata.ExternalVersionIdentifier == "630253062", "version identifier stays a string");
Check(versionMetadata.DisplayVersion == "0.8.0", "human-readable version metadata contract");
Check(versionMetadata.VersionLabel == "v0.8.0", "human-readable version receives v prefix");
Check(
    versionMetadata.ReleaseDate == new DateTimeOffset(2014, 7, 23, 19, 48, 8, TimeSpan.Zero),
    "version release date contract");
Check(versionMetadata.ReleaseDateLabel == "Released 2014-07-23", "localized release date label");
Check(
    Throws<InvalidDataException>(() =>
        IpatoolJsonParser.ParseVersionMetadata(versionMetadataOutput, "630253099")),
    "mismatched version metadata is rejected");
Check(
    Throws<InvalidDataException>(() =>
        IpatoolJsonParser.ParseVersionMetadata(
            """{"externalVersionID":"630253062","displayVersion":"0.8.0","releaseDate":"not-a-date","success":true}""",
            "630253062")),
    "invalid version release date is rejected");
var unresolvedVersion = StoreAppVersion.Unresolved("630253063", "temporary failure");
Check(!unresolvedVersion.HasMetadata, "failed metadata remains an explicit unresolved version");
Check(
    unresolvedVersion.IdentifierLabel == "Version identifier 630253063",
    "unresolved version preserves the download identifier");

var pinnedAmd64Ipatool = ToolBootstrapService.GetPinnedIpatoolPackage(Architecture.X64);
Check(pinnedAmd64Ipatool.Version == "v2.3.0", "ipatool bootstrap version is pinned");
Check(
    pinnedAmd64Ipatool.ArchiveName == "ipatool-2.3.0-windows-amd64.tar.gz",
    "ipatool AMD64 archive name is pinned");
Check(
    pinnedAmd64Ipatool.Sha256 ==
    "eaf208f0fee964a82f14f8eda60c4b0568fe555ad97729bb74277d3d7c0e4d54",
    "ipatool AMD64 checksum is pinned");
var pinnedArm64Ipatool = ToolBootstrapService.GetPinnedIpatoolPackage(Architecture.Arm64);
Check(
    pinnedArm64Ipatool.DownloadUrl.EndsWith(
        "/v2.3.0/ipatool-2.3.0-windows-arm64.tar.gz",
        StringComparison.Ordinal),
    "ipatool ARM64 download remains on the reviewed release");
Check(
    pinnedArm64Ipatool.Sha256 ==
    "690d94332802f5fca604cce29ac9762089c7271c30a68e64eeb462c605e1fa07",
    "ipatool ARM64 checksum is pinned");
Check(
    Throws<PlatformNotSupportedException>(() =>
        ToolBootstrapService.GetPinnedIpatoolPackage(Architecture.X86)),
    "unsupported ipatool architectures are rejected");

const string deviceUdid = "00008110-001234567890801E";
const string ipaPath = @"D:\Downloads\Example App.ipa";
Check(
    DeviceService.BuildModernPairArguments(deviceUdid).SequenceEqual(
    [
        "--udid",
        deviceUdid,
        "pair",
        deviceUdid,
        "--name",
        "IPA Bridge"
    ]),
    "idevice-tools pairing passes the selected UDID to the pair subcommand");
Check(
    DeviceService.BuildLibimobiledeviceInstallArguments(
        deviceUdid,
        ipaPath,
        useLegacySyntax: false).SequenceEqual(
    [
        "-u",
        deviceUdid,
        "install",
        ipaPath
    ]),
    "current ideviceinstaller install arguments remain exact");
Check(
    DeviceService.UsesLegacyInstallSyntax(
        "ERROR: No mode/command was supplied.\r\nUsage: ideviceinstaller OPTIONS"),
    "ideviceinstaller 1.1.1 no-mode response enables the legacy fallback");
Check(
    DeviceService.BuildLibimobiledeviceInstallArguments(
        deviceUdid,
        ipaPath,
        useLegacySyntax: true).SequenceEqual(
    [
        "-u",
        deviceUdid,
        "-i",
        ipaPath
    ]),
    "legacy ideviceinstaller fallback uses the -i syntax");
Check(
    !DeviceService.UsesLegacyInstallSyntax(
        "ERROR: Install failed. Got error ApplicationVerificationFailed."),
    "device-side installation failures do not trigger a legacy syntax retry");

var readyAppleSupport = new AppleDeviceSupportStatus
{
    HasBeenChecked = true,
    IsAppleDevicesInstalled = true,
    IsUsbDriverInstalled = true,
    IsTransportEndpointReachable = true,
    IsTransportServiceRegistered = true,
    IsBackendProbeSuccessful = true,
    BackendName = "idevice-tools"
};
Check(readyAppleSupport.IsReady, "successful idevice_id probe provides operational device support");
Check(readyAppleSupport.HasCompleteUsbSupport, "ready Apple support includes the USB driver");
Check(readyAppleSupport.OverallLabel == "Ready", "ready Apple support label");
var transportOnlyAppleSupport = readyAppleSupport with { IsUsbDriverInstalled = false };
Check(
    transportOnlyAppleSupport.IsReady,
    "driver inventory does not block a working Apple transport");
Check(
    !transportOnlyAppleSupport.HasCompleteUsbSupport,
    "missing USB driver remains visible as a diagnostic");
var endpointOnlyAppleSupport = readyAppleSupport with
{
    IsBackendProbeSuccessful = null,
    BackendName = null
};
Check(
    !endpointOnlyAppleSupport.IsReady,
    "a reachable TCP endpoint alone does not establish operational readiness");
var failedBackendAppleSupport = readyAppleSupport with
{
    IsBackendProbeSuccessful = false,
    BackendProbeError = "idevice_id could not connect"
};
Check(!failedBackendAppleSupport.IsReady, "failed idevice_id probe is not ready");
Check(
    failedBackendAppleSupport.BackendTransportLabel == "Unavailable — idevice-tools",
    "failed backend probe remains visible");
Check(
    failedBackendAppleSupport.TransportEndpointLabel == "Endpoint reachable — Diagnostic only",
    "TCP endpoint reachability is labeled as diagnostic only");

var appleDevicesArguments = SystemPrerequisiteService.BuildAppleDevicesWingetInstallArguments();
Check(
    appleDevicesArguments.SequenceEqual(
    [
        "install",
        "--id",
        "9NP83LWLPZ9K",
        "--exact",
        "--source",
        "msstore",
        "--accept-source-agreements",
        "--accept-package-agreements",
        "--silent",
        "--disable-interactivity"
    ]),
    "Apple Devices installation is pinned to the Microsoft Store product");
Check(
    SystemPrerequisiteService.AppleDevicesStoreUri ==
    "ms-windows-store://pdp/?ProductId=9NP83LWLPZ9K",
    "Apple Devices Store fallback URI contract");

var temporaryDriverStore = Path.Combine(
    Path.GetTempPath(),
    $"ipa-bridge-driver-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(temporaryDriverStore);
try
{
    var missingDriver = SystemPrerequisiteService.DetectAppleUsbDriver(temporaryDriverStore);
    Check(missingDriver.IsInstalled == false, "readable driver inventory can report no Apple driver");

    var legacyPackage = Path.Combine(temporaryDriverStore, "usbaapl64.inf_amd64_legacy");
    Directory.CreateDirectory(legacyPackage);
    File.WriteAllText(
        Path.Combine(legacyPackage, "usbaapl64.inf"),
        "[Version]\r\nDriverVer=05/19/2017,6.0.9999.69\r\n");
    var modernPackage = Path.Combine(temporaryDriverStore, "appleusb.inf_amd64_modern");
    Directory.CreateDirectory(modernPackage);
    File.WriteAllText(
        Path.Combine(modernPackage, "appleusb.inf"),
        "[Version]\r\nDriverVer=06/14/2023,538.0.0.0\r\n");

    var detectedDriver = SystemPrerequisiteService.DetectAppleUsbDriver(temporaryDriverStore);
    Check(detectedDriver.IsInstalled == true, "Apple USB driver package is detected");
    Check(detectedDriver.Version == "538.0.0.0", "newest Apple USB driver version is selected");
    Check(detectedDriver.PackageName == "appleusb.inf", "modern Apple USB driver is identified");
}
finally
{
    Directory.Delete(temporaryDriverStore, recursive: true);
}

var unavailableDriverInventory = SystemPrerequisiteService.DetectAppleUsbDriver(
    Path.Combine(Path.GetTempPath(), $"ipa-bridge-missing-driver-store-{Guid.NewGuid():N}"));
Check(
    unavailableDriverInventory.IsInstalled is null,
    "unavailable driver inventory is not misreported as a missing driver");

var liveAppleSupport = await new SystemPrerequisiteService()
    .GetAppleDeviceSupportStatusAsync();
Check(liveAppleSupport.HasBeenChecked, "live Apple device support probe completes");

var temporaryTools = Path.Combine(
    Path.GetTempPath(),
    $"ipa-bridge-tools-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(temporaryTools);
try
{
    var configurationService = new ConfigurationService();
    configurationService.Current.DeviceToolsDirectory = temporaryTools;
    var locations = new ToolLocationService(configurationService);

    File.WriteAllBytes(Path.Combine(temporaryTools, "ideviceinstaller.exe"), []);
    File.WriteAllBytes(Path.Combine(temporaryTools, "idevice_id.exe"), []);
    Check(
        locations.ResolveDeviceTools().DirectoryPath != temporaryTools,
        "incomplete libimobiledevice directory is rejected");

    File.WriteAllBytes(Path.Combine(temporaryTools, "idevicepair.exe"), []);
    File.WriteAllBytes(Path.Combine(temporaryTools, "ideviceinfo.exe"), []);
    Check(
        locations.ResolveDeviceTools().Backend == IPABridge.Models.DeviceBackend.Libimobiledevice,
        "complete libimobiledevice directory is accepted");
    var failedExecutableProbe = await new SystemPrerequisiteService(new ProcessRunner(), locations)
        .GetAppleDeviceSupportStatusAsync();
    Check(
        failedExecutableProbe.IsBackendProbeSuccessful == false && !failedExecutableProbe.IsReady,
        "a configured backend must successfully execute idevice_id before becoming ready");

    File.WriteAllBytes(Path.Combine(temporaryTools, "idevice-tools.exe"), []);
    Check(
        locations.ResolveDeviceTools().Backend == IPABridge.Models.DeviceBackend.ModernIdeviceTools,
        "modern device backend is detected");

    var sharedDeviceGate = new DeviceEnvironmentOperationGate();
    var processRunner = new ProcessRunner();
    var prerequisiteService = new SystemPrerequisiteService(processRunner, locations);
    using var bootstrapService = new ToolBootstrapService(configurationService);
    using var devicesViewModel = new IPABridge.ViewModels.DevicesViewModel(
        configurationService,
        new DeviceService(locations, processRunner),
        prerequisiteService,
        sharedDeviceGate,
        (_, _, _) => { });
    using var settingsViewModel = new IPABridge.ViewModels.SettingsViewModel(
        configurationService,
        locations,
        bootstrapService,
        new IpatoolService(locations, processRunner, new ConPtyProcessRunner()),
        prerequisiteService,
        sharedDeviceGate,
        (_, _, _) => { });
    await settingsViewModel.RefreshStatusAsync();
    Check(
        settingsViewModel.DeviceToolsStatus == "Configured — idevice-tools",
        "file-only device tools detection is labeled configured rather than ready");

    devicesViewModel.ApplyAppleDeviceSupportStatus(readyAppleSupport);
    var staleDevice = new ConnectedDevice
    {
        Udid = deviceUdid,
        Name = "Smoke Test iPhone",
        IsPaired = false
    };
    devicesViewModel.Items.Add(staleDevice);
    devicesViewModel.SelectedDevice = staleDevice;
    Check(
        devicesViewModel.PairCommand.CanExecute(null),
        "pairing is enabled only with operational support, tools, and a selected device");

    var deviceCommandNotifications = 0;
    var settingsCommandNotifications = 0;
    devicesViewModel.RefreshCommand.CanExecuteChanged += (_, _) => deviceCommandNotifications++;
    settingsViewModel.RefreshAppleDeviceSupportCommand.CanExecuteChanged +=
        (_, _) => settingsCommandNotifications++;
    using (var sharedLease = sharedDeviceGate.TryEnter())
    {
        Check(
            sharedLease is not null &&
            !devicesViewModel.RefreshCommand.CanExecute(null) &&
            !devicesViewModel.PairCommand.CanExecute(null) &&
            !settingsViewModel.RefreshAppleDeviceSupportCommand.CanExecute(null) &&
            !settingsViewModel.InstallAppleDevicesCommand.CanExecute(null) &&
            !settingsViewModel.InstallDeviceToolsCommand.CanExecute(null),
            "shared gate disables device and Settings environment commands across views");
    }

    Check(
        deviceCommandNotifications == 2 && settingsCommandNotifications == 2 &&
        devicesViewModel.RefreshCommand.CanExecute(null) &&
        settingsViewModel.RefreshAppleDeviceSupportCommand.CanExecute(null),
        "cross-view commands are notified when the device environment becomes available");

    devicesViewModel.ApplyAppleDeviceSupportStatus(failedBackendAppleSupport);
    Check(
        devicesViewModel.Items.Count == 0 && devicesViewModel.SelectedDevice is null,
        "readiness loss clears stale connected devices and selection");
    Check(
        !devicesViewModel.PairCommand.CanExecute(null),
        "readiness loss disables pairing");
}
finally
{
    Directory.Delete(temporaryTools, recursive: true);
}

var temporaryConfigurationRoot = Path.Combine(
    Path.GetTempPath(),
    $"ipa-bridge-configuration-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(temporaryConfigurationRoot);
try
{
    var blockedConfigurationPath = Path.Combine(temporaryConfigurationRoot, "settings.json");
    Directory.CreateDirectory(blockedConfigurationPath);
    var isolatedConfiguration = new ConfigurationService(
        blockedConfigurationPath,
        Path.Combine(temporaryConfigurationRoot, "Downloads"));
    var saveFailed = false;
    try
    {
        await isolatedConfiguration.SaveAsync();
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        saveFailed = true;
    }

    Check(saveFailed, "configuration save failure is surfaced");
    Check(
        !Directory.EnumerateFiles(
                temporaryConfigurationRoot,
                "settings.json.*.tmp",
                SearchOption.TopDirectoryOnly)
            .Any(),
        "failed configuration save removes its temporary file");
}
finally
{
    Directory.Delete(temporaryConfigurationRoot, recursive: true);
}

var executable = Environment.ProcessPath;
if (string.IsNullOrWhiteSpace(executable))
{
    failures.Add("ConPTY executable path");
}
else
{
    var runner = new ConPtyProcessRunner();
    var result = await runner.RunAsync(
        executable,
        ["--prompt-child"],
        [new ConPtyPrompt("password", "enter password:", "temporary-secret")]);
    if (!result.IsSuccess || !result.Output.Contains("\"success\":true", StringComparison.Ordinal))
    {
        Console.WriteLine(
            $"INFO  ConPTY exit={result.ExitCode}, missingPrompt={result.MissingPromptKey ?? "<none>"}, output={result.Output}");
    }

    Check(result.IsSuccess, "ConPTY process completes successfully");
    Check(!result.Output.Contains("temporary-secret", StringComparison.Ordinal), "ConPTY redacts prompt secrets");
    Check(result.Output.Contains("\"success\":true", StringComparison.Ordinal), "ConPTY captures child output");

    var temporaryDownloads = Path.Combine(
        Path.GetTempPath(),
        $"ipa-bridge-download-smoke-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temporaryDownloads);
    var originalFakeIpatoolMode = Environment.GetEnvironmentVariable(
        "IPA_BRIDGE_SMOKE_FAKE_IPATOOL");
    try
    {
        var fakeConfiguration = new ConfigurationService();
        fakeConfiguration.Current.IpatoolPath = executable;
        var fakeIpatool = new IpatoolService(
            new ToolLocationService(fakeConfiguration),
            new ProcessRunner(),
            new ConPtyProcessRunner());
        var fakeApp = new StoreApp
        {
            BundleIdentifier = "com.example.cleanup",
            Name = "Cleanup Test"
        };

        Environment.SetEnvironmentVariable(
            "IPA_BRIDGE_SMOKE_FAKE_IPATOOL",
            "download-failure");
        var downloadFailed = false;
        try
        {
            await fakeIpatool.DownloadAsync(
                fakeApp,
                temporaryDownloads,
                "temporary-vault-secret");
        }
        catch (InvalidOperationException exception)
            when (exception.Message.Contains("simulated download failure", StringComparison.Ordinal))
        {
            downloadFailed = true;
        }

        Check(downloadFailed, "failed ipatool download is surfaced");
        Check(
            !Directory.EnumerateFileSystemEntries(
                    temporaryDownloads,
                    "*",
                    SearchOption.AllDirectories)
                .Any(),
            "failed ipatool download removes partial IPA and temporary files");

        Environment.SetEnvironmentVariable(
            "IPA_BRIDGE_SMOKE_FAKE_IPATOOL",
            "download-success");
        var completedIpa = await fakeIpatool.DownloadAsync(
            fakeApp,
            temporaryDownloads,
            "temporary-vault-secret");
        Check(File.Exists(completedIpa), "completed IPA is moved into the download library");
        Check(
            File.ReadAllText(completedIpa) == "complete archive",
            "completed IPA retains the staged download contents");
        Check(
            !Directory.Exists(Path.Combine(temporaryDownloads, ".ipa-bridge-staging")),
            "successful ipatool download removes its staging directory");
        Check(
            Directory.EnumerateFiles(temporaryDownloads, "*.ipa", SearchOption.TopDirectoryOnly)
                .Count() == 1,
            "only the completed IPA is visible to the library scanner");
    }
    finally
    {
        Environment.SetEnvironmentVariable(
            "IPA_BRIDGE_SMOKE_FAKE_IPATOOL",
            originalFakeIpatoolMode);
        Directory.Delete(temporaryDownloads, recursive: true);
    }

    var officialIpatool = Environment.GetEnvironmentVariable("IPA_BRIDGE_TEST_IPATOOL");
    var officialIpatoolRequired = string.Equals(
        Environment.GetEnvironmentVariable("IPA_BRIDGE_REQUIRE_TEST_IPATOOL"),
        "1",
        StringComparison.Ordinal);
    if (officialIpatoolRequired &&
        (string.IsNullOrWhiteSpace(officialIpatool) || !File.Exists(officialIpatool)))
    {
        Check(false, "required official ipatool contract binary is available");
    }

    if (!string.IsNullOrWhiteSpace(officialIpatool) && File.Exists(officialIpatool))
    {
        var temporaryProfile = Path.Combine(
            Path.GetTempPath(),
            $"ipa-bridge-ipatool-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryProfile);
        var temporaryHomeDrive = Path.GetPathRoot(temporaryProfile)!
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var homeEnvironment = new Dictionary<string, string>
        {
            ["USERPROFILE"] = temporaryProfile,
            ["HOME"] = temporaryProfile,
            ["HOMEDRIVE"] = temporaryHomeDrive,
            ["HOMEPATH"] = temporaryProfile[temporaryHomeDrive.Length..]
        };
        var originalHomeEnvironment = homeEnvironment.Keys.ToDictionary(
            key => key,
            Environment.GetEnvironmentVariable);
        try
        {
            foreach (var pair in homeEnvironment)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }

            var processRunner = new ProcessRunner();
            using var offlineContractTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var versionResult = await processRunner.RunAsync(
                officialIpatool,
                ["--version"],
                cancellationToken: offlineContractTimeout.Token);
            Check(
                versionResult.IsSuccess &&
                versionResult.CombinedOutput.Contains("ipatool version 2.3.0", StringComparison.Ordinal),
                "official ipatool binary matches the pinned version");

            var helpResult = await processRunner.RunAsync(
                officialIpatool,
                ["--help"],
                cancellationToken: offlineContractTimeout.Token);
            Check(
                helpResult.IsSuccess &&
                new[] { "download", "get-version-metadata", "list-versions", "search" }
                    .All(command => helpResult.CombinedOutput.Contains(command, StringComparison.Ordinal)),
                "official ipatool command surface matches IPA Bridge");

            var jsonErrorResult = await processRunner.RunAsync(
                officialIpatool,
                ["--format", "json", "--non-interactive", "search"],
                cancellationToken: offlineContractTimeout.Token);
            Check(
                jsonErrorResult.ExitCode != 0 &&
                IpatoolJsonParser.FindError(jsonErrorResult.CombinedOutput) ==
                "accepts 1 arg(s), received 0",
                "official ipatool JSON error contract is parsed");

            using var promptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var ipatoolResult = await runner.RunAsync(
                officialIpatool,
                ["--format", "json", "auth", "login", "--email", "nobody@example.invalid"],
                [
                    new ConPtyPrompt("apple-password", "enter password:", null)
                ],
                cancellationToken: promptTimeout.Token);
            Check(
                ipatoolResult.MissingPromptKey == "apple-password" &&
                ipatoolResult.Output.Contains("enter password:", StringComparison.OrdinalIgnoreCase),
                "official ipatool terminal password prompt is captured");
        }
        finally
        {
            foreach (var pair in originalHomeEnvironment)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }

            if (Directory.Exists(temporaryProfile))
            {
                Directory.Delete(temporaryProfile, recursive: true);
            }
        }
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"{failures.Count} smoke test(s) failed:");
    foreach (var failure in failures)
    {
        Console.Error.WriteLine($"- {failure}");
    }

    return 1;
}

Console.WriteLine();
Console.WriteLine("All IPA Bridge smoke tests passed.");
return 0;
