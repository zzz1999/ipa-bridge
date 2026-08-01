using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IPABridge.Infrastructure;
using IPABridge.Models;
using IPABridge.Services;
using IPABridge.ViewModels;

var fakeIpatoolMode = Environment.GetEnvironmentVariable("IPA_BRIDGE_SMOKE_FAKE_IPATOOL");
if (fakeIpatoolMode == "login-wait")
{
    Console.Write("enter password:");
    _ = Console.ReadLine();
    Console.Write("enter passphrase to unlock");
    _ = Console.ReadLine();
    var markerPath = Environment.GetEnvironmentVariable(
        "IPA_BRIDGE_SMOKE_LOGIN_WAIT_MARKER");
    if (!string.IsNullOrWhiteSpace(markerPath))
    {
        File.WriteAllText(markerPath, "waiting");
    }

    await Task.Delay(TimeSpan.FromMinutes(2));
    return 0;
}

if (fakeIpatoolMode is "download-failure" or "download-success" or "search-success")
{
    Console.Write("enter passphrase to unlock");
    var passphrase = Console.ReadLine();
    if (passphrase != "temporary-vault-secret")
    {
        Console.WriteLine("{\"level\":\"error\",\"error\":\"wrong passphrase\",\"success\":false}");
        return 3;
    }

    if (args.Contains("auth", StringComparer.Ordinal) &&
        args.Contains("info", StringComparer.Ordinal))
    {
        Console.WriteLine(
            "{\"level\":\"info\",\"name\":\"Smoke Account\",\"email\":\"cleanup@example.invalid\",\"success\":true}");
        return 0;
    }

    if (fakeIpatoolMode == "search-success")
    {
        string[] expectedSearchArguments =
        [
            "--format",
            "json",
            "search",
            "bridge",
            "--limit",
            "25"
        ];
        if (!args.SequenceEqual(expectedSearchArguments, StringComparer.Ordinal))
        {
            Console.WriteLine("{\"level\":\"error\",\"error\":\"invalid pinned search arguments\",\"success\":false}");
            return 5;
        }

        Console.WriteLine(
            "{\"level\":\"info\",\"count\":1,\"apps\":[{\"id\":1,\"bundleID\":\"com.example.bridge\",\"name\":\"Bridge\",\"version\":\"1.0\",\"price\":0}]}");
        return 0;
    }

    var outputArgumentIndex = Array.IndexOf(args, "--output");
    if (outputArgumentIndex < 0 || outputArgumentIndex + 1 >= args.Length)
    {
        Console.WriteLine("{\"level\":\"error\",\"error\":\"missing output path\",\"success\":false}");
        return 2;
    }

    if (!args.Contains("--purchase", StringComparer.Ordinal) ||
        args.Contains("--platform", StringComparer.Ordinal))
    {
        Console.WriteLine("{\"level\":\"error\",\"error\":\"invalid pinned download arguments\",\"success\":false}");
        return 4;
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

if (args is ["--environment-child"])
{
    Console.WriteLine($"marker={Environment.GetEnvironmentVariable("IPA_BRIDGE_ACCOUNT_ENVIRONMENT_SMOKE")}");
    Console.WriteLine($"drive={Environment.GetEnvironmentVariable("HOMEDRIVE")}");
    Console.WriteLine($"path={Environment.GetEnvironmentVariable("HOMEPATH")}");
    return 0;
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

async Task<bool> ThrowsAsync<TException>(Func<Task> action)
    where TException : Exception
{
    try
    {
        await action();
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

var wpfBindingSmokeExecutable = Path.ChangeExtension(
    typeof(MainViewModel).Assembly.Location,
    ".exe");
var wpfBindingSmokeResultPath = Path.Combine(
    Path.GetTempPath(),
    $"ipa-bridge-wpf-binding-smoke-{Guid.NewGuid():N}.txt");
ProcessResult? wpfBindingSmokeProcess = null;
string? wpfBindingSmokeReport = null;
Check(File.Exists(wpfBindingSmokeExecutable), "WPF application smoke executable is available");
try
{
    if (File.Exists(wpfBindingSmokeExecutable))
    {
        using var wpfBindingSmokeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        wpfBindingSmokeProcess = await new ProcessRunner().RunAsync(
            wpfBindingSmokeExecutable,
            ["--wpf-binding-smoke"],
            environment: new Dictionary<string, string>
            {
                ["IPA_BRIDGE_WPF_SMOKE_RESULT"] = wpfBindingSmokeResultPath
            },
            cancellationToken: wpfBindingSmokeTimeout.Token);
        if (File.Exists(wpfBindingSmokeResultPath))
        {
            wpfBindingSmokeReport = File.ReadAllText(wpfBindingSmokeResultPath);
        }
    }
}
catch (Exception exception)
{
    wpfBindingSmokeReport = exception.ToString();
}
finally
{
    File.Delete(wpfBindingSmokeResultPath);
}

Check(
    wpfBindingSmokeProcess?.IsSuccess == true,
    "all WPF page templates complete binding evaluation");
Check(wpfBindingSmokeReport == "PASS", "all WPF page templates load without a binding exception");
if (wpfBindingSmokeReport is not null and not "PASS")
{
    Console.Error.WriteLine(wpfBindingSmokeReport);
}

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

var accountInfo = IpatoolJsonParser.ParseAccountInfo(
    """{"level":"info","name":"Example User","email":"user@example.invalid","success":true}""");
Check(
    accountInfo.Email == "user@example.invalid" && accountInfo.Name == "Example User",
    "ipatool account identity contract");
Check(
    Throws<InvalidDataException>(() => IpatoolJsonParser.ParseAccountInfo(
        """{"level":"info","name":"Missing Email","success":true}""")),
    "ipatool account identity requires an email address");
Check(
    IpatoolService.IsAccountNotFoundError(
        "failed to get account: failed to get item: The specified item could not be found in the keyring"),
    "pinned ipatool missing-account error is recognized");
Check(
    !IpatoolService.IsAccountNotFoundError("failed to decrypt keyring: incorrect passphrase"),
    "credential-vault errors are not mistaken for a missing account");

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

    configurationService.Current.AutomaticallyRefreshDevices = false;
    devicesViewModel.ApplyAutomaticRefreshPreference();
    Check(
        !devicesViewModel.IsAutomaticRefreshTimerEnabled,
        "automatic device refresh timer stops when the preference is disabled");
    configurationService.Current.AutomaticallyRefreshDevices = true;
    devicesViewModel.ApplyAutomaticRefreshPreference();
    Check(
        devicesViewModel.IsAutomaticRefreshTimerEnabled,
        "automatic device refresh timer starts when the preference is enabled");
    configurationService.Current.AutomaticallyRefreshDevices = false;
    devicesViewModel.ApplyAutomaticRefreshPreference();
    Check(
        !devicesViewModel.IsAutomaticRefreshTimerEnabled,
        "automatic device refresh timer supports repeated preference changes");

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
    await isolatedConfiguration.LoadAsync();
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

var encryptedConfigurationRoot = Path.Combine(
    Path.GetTempPath(),
    $"ipa-bridge-encrypted-configuration-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(encryptedConfigurationRoot);
try
{
    var secureConfigurationPath = Path.Combine(
        encryptedConfigurationRoot,
        "settings.secure.json");
    var legacyConfigurationPath = Path.Combine(encryptedConfigurationRoot, "settings.json");
    var keyPath = Path.Combine(encryptedConfigurationRoot, "master-key.v1");
    var downloadPath = Path.Combine(encryptedConfigurationRoot, "Downloads");
    var keyProtector = new SmokeKeyProtector();
    var randomByteGenerator = new SmokeRandomByteGenerator();
    var encryptedConfiguration = new ConfigurationService(
        secureConfigurationPath,
        legacyConfigurationPath,
        keyPath,
        downloadPath,
        keyProtector,
        randomByteGenerator);

    await encryptedConfiguration.LoadAsync();
    Check(File.Exists(keyPath), "first launch creates a protected local settings key");
    Check(
        keyProtector.LastProtectedPlaintext is { Length: LocalDataProtectionService.MasterKeySize },
        "first-launch settings key is 256 bits");
    Check(
        keyProtector.LastProtectedPlaintext is not null &&
        !File.ReadAllBytes(keyPath).AsSpan().SequenceEqual(keyProtector.LastProtectedPlaintext),
        "settings key is protected before it is written to disk");

    const string emailCanary = "encrypted-settings-canary@example.invalid";
    const string secondEmailCanary = "second-encrypted-settings-canary@example.invalid";
    const string firstAccountId = "11111111111111111111111111111111";
    const string secondAccountId = "22222222222222222222222222222222";
    const string toolCanary = "SECRET_TOOL_PATH_CANARY";
    const string deviceToolCanary = "SECRET_DEVICE_PATH_CANARY";
    encryptedConfiguration.Current.IpatoolPath = Path.Combine(
        encryptedConfigurationRoot,
        toolCanary,
        "ipatool.exe");
    encryptedConfiguration.Current.DeviceToolsDirectory = Path.Combine(
        encryptedConfigurationRoot,
        deviceToolCanary);
    encryptedConfiguration.Current.DownloadDirectory = downloadPath;
    encryptedConfiguration.Current.AppleAccounts =
    [
        new AppleAccountProfile { Id = firstAccountId, Email = emailCanary },
        new AppleAccountProfile { Id = secondAccountId, Email = secondEmailCanary }
    ];
    encryptedConfiguration.Current.SelectedAppleAccountId = secondAccountId;
    encryptedConfiguration.Current.AutomaticallyRefreshDevices = false;
    await encryptedConfiguration.SaveAsync();

    var firstEncryptedFile = File.ReadAllBytes(secureConfigurationPath);
    var firstEncryptedText = Encoding.UTF8.GetString(firstEncryptedFile);
    Check(
        firstEncryptedText.Contains(LocalDataProtectionService.EnvelopeFormat, StringComparison.Ordinal) &&
        firstEncryptedText.Contains(LocalDataProtectionService.EnvelopeAlgorithm, StringComparison.Ordinal),
        "settings use a versioned AES-256-GCM envelope");
    Check(
        !firstEncryptedText.Contains(emailCanary, StringComparison.Ordinal) &&
        !firstEncryptedText.Contains(secondEmailCanary, StringComparison.Ordinal) &&
        !firstEncryptedText.Contains(toolCanary, StringComparison.Ordinal) &&
        !firstEncryptedText.Contains(deviceToolCanary, StringComparison.Ordinal),
        "encrypted settings do not expose saved email or path values");

    var reopenedConfiguration = new ConfigurationService(
        secureConfigurationPath,
        legacyConfigurationPath,
        keyPath,
        downloadPath,
        new SmokeKeyProtector(),
        new SmokeRandomByteGenerator());
    var reopened = await reopenedConfiguration.LoadAsync();
    Check(
        reopened.IpatoolPath == encryptedConfiguration.Current.IpatoolPath &&
        reopened.DeviceToolsDirectory == encryptedConfiguration.Current.DeviceToolsDirectory &&
        reopened.DownloadDirectory == encryptedConfiguration.Current.DownloadDirectory &&
        reopened.SchemaVersion == AppConfiguration.CurrentSchemaVersion &&
        reopened.AppleAccounts.Count == 2 &&
        reopened.AppleAccounts[0].Id == firstAccountId &&
        reopened.AppleAccounts[0].Email == emailCanary &&
        reopened.AppleAccounts[1].Id == secondAccountId &&
        reopened.AppleAccounts[1].Email == secondEmailCanary &&
        reopened.SelectedAppleAccountId == secondAccountId &&
        !reopened.AutomaticallyRefreshDevices,
        "encrypted settings round-trip every configuration property");

    await encryptedConfiguration.SaveAsync();
    var secondEncryptedFile = File.ReadAllBytes(secureConfigurationPath);
    Check(
        !firstEncryptedFile.AsSpan().SequenceEqual(secondEncryptedFile),
        "saving identical settings uses a fresh authenticated-encryption nonce");

    var validEncryptedFile = secondEncryptedFile.ToArray();
    var tamperedEnvelope = JsonNode.Parse(validEncryptedFile)!.AsObject();
    var ciphertext = tamperedEnvelope["ciphertext"]!.GetValue<string>();
    var replacementCharacter = ciphertext[0] == 'A' ? 'B' : 'A';
    tamperedEnvelope["ciphertext"] = replacementCharacter + ciphertext[1..];
    File.WriteAllText(secureConfigurationPath, tamperedEnvelope.ToJsonString());
    var tamperedConfiguration = new ConfigurationService(
        secureConfigurationPath,
        legacyConfigurationPath,
        keyPath,
        downloadPath,
        new SmokeKeyProtector(),
        new SmokeRandomByteGenerator());
    Check(
        await ThrowsAsync<InvalidDataException>(() => tamperedConfiguration.LoadAsync()),
        "tampered settings ciphertext is rejected");
    var rejectedEncryptedFile = File.ReadAllBytes(secureConfigurationPath);
    Check(
        await ThrowsAsync<InvalidOperationException>(() => tamperedConfiguration.SaveAsync()),
        "a failed encrypted settings load blocks later saves");
    Check(
        File.ReadAllBytes(secureConfigurationPath).AsSpan().SequenceEqual(rejectedEncryptedFile),
        "rejected settings ciphertext is not silently overwritten");

    await File.WriteAllBytesAsync(secureConfigurationPath, validEncryptedFile);
    var tamperedTagEnvelope = JsonNode.Parse(validEncryptedFile)!.AsObject();
    var authenticationTag = tamperedTagEnvelope["authenticationTag"]!.GetValue<string>();
    var tagReplacementCharacter = authenticationTag[0] == 'A' ? 'B' : 'A';
    tamperedTagEnvelope["authenticationTag"] =
        tagReplacementCharacter + authenticationTag[1..];
    File.WriteAllText(secureConfigurationPath, tamperedTagEnvelope.ToJsonString());
    var tamperedTagConfiguration = new ConfigurationService(
        secureConfigurationPath,
        legacyConfigurationPath,
        keyPath,
        downloadPath,
        new SmokeKeyProtector(),
        new SmokeRandomByteGenerator());
    Check(
        await ThrowsAsync<InvalidDataException>(() => tamperedTagConfiguration.LoadAsync()),
        "tampered settings authentication tag is rejected");

    await File.WriteAllBytesAsync(secureConfigurationPath, validEncryptedFile);
    var tamperedNonceEnvelope = JsonNode.Parse(validEncryptedFile)!.AsObject();
    var nonce = tamperedNonceEnvelope["nonce"]!.GetValue<string>();
    var nonceReplacementCharacter = nonce[0] == 'A' ? 'B' : 'A';
    tamperedNonceEnvelope["nonce"] = nonceReplacementCharacter + nonce[1..];
    File.WriteAllText(secureConfigurationPath, tamperedNonceEnvelope.ToJsonString());
    var tamperedNonceConfiguration = new ConfigurationService(
        secureConfigurationPath,
        legacyConfigurationPath,
        keyPath,
        downloadPath,
        new SmokeKeyProtector(),
        new SmokeRandomByteGenerator());
    Check(
        await ThrowsAsync<InvalidDataException>(() => tamperedNonceConfiguration.LoadAsync()),
        "tampered settings nonce is rejected");

    await File.WriteAllBytesAsync(secureConfigurationPath, validEncryptedFile);
    var savedProtectedKey = File.ReadAllBytes(keyPath);
    var corruptedProtectedKey = savedProtectedKey.ToArray();
    corruptedProtectedKey[0] ^= 0x01;
    await File.WriteAllBytesAsync(keyPath, corruptedProtectedKey);
    var corruptedKeyConfiguration = new ConfigurationService(
        secureConfigurationPath,
        legacyConfigurationPath,
        keyPath,
        downloadPath,
        new SmokeKeyProtector(),
        new SmokeRandomByteGenerator());
    Check(
        await ThrowsAsync<InvalidDataException>(() => corruptedKeyConfiguration.LoadAsync()),
        "a corrupted local settings key cannot authenticate encrypted settings");
    Check(
        File.ReadAllBytes(keyPath).AsSpan().SequenceEqual(corruptedProtectedKey),
        "a corrupted local settings key is not silently replaced");

    await File.WriteAllBytesAsync(keyPath, savedProtectedKey);
    File.Delete(keyPath);
    var missingKeyRandom = new SmokeRandomByteGenerator();
    var missingKeyConfiguration = new ConfigurationService(
        secureConfigurationPath,
        legacyConfigurationPath,
        keyPath,
        downloadPath,
        new SmokeKeyProtector(),
        missingKeyRandom);
    Check(
        await ThrowsAsync<InvalidDataException>(() => missingKeyConfiguration.LoadAsync()),
        "encrypted settings fail closed when their local key is missing");
    Check(
        !File.Exists(keyPath) && missingKeyRandom.RequestCount == 0,
        "a missing key for existing encrypted settings is not silently replaced");
    await File.WriteAllBytesAsync(keyPath, savedProtectedKey);

    var unsupportedEnvelope = JsonNode.Parse(validEncryptedFile)!.AsObject();
    unsupportedEnvelope["version"] = LocalDataProtectionService.EnvelopeVersion + 1;
    File.WriteAllText(secureConfigurationPath, unsupportedEnvelope.ToJsonString());
    var unsupportedConfiguration = new ConfigurationService(
        secureConfigurationPath,
        legacyConfigurationPath,
        keyPath,
        downloadPath,
        new SmokeKeyProtector(),
        new SmokeRandomByteGenerator());
    Check(
        await ThrowsAsync<InvalidDataException>(() => unsupportedConfiguration.LoadAsync()),
        "unknown encrypted settings versions are rejected");
    await File.WriteAllBytesAsync(secureConfigurationPath, validEncryptedFile);

    var conflictingLegacyConfiguration = new AppConfiguration
    {
        AppleAccountEmail = "newer-legacy-conflict@example.invalid",
        DownloadDirectory = downloadPath
    };
    await File.WriteAllTextAsync(
        legacyConfigurationPath,
        JsonSerializer.Serialize(conflictingLegacyConfiguration));
    var secureBeforeConflict = File.ReadAllBytes(secureConfigurationPath);
    var legacyBeforeConflict = File.ReadAllBytes(legacyConfigurationPath);
    var conflictConfiguration = new ConfigurationService(
        secureConfigurationPath,
        legacyConfigurationPath,
        keyPath,
        downloadPath,
        new SmokeKeyProtector(),
        new SmokeRandomByteGenerator());
    Check(
        await ThrowsAsync<InvalidDataException>(() => conflictConfiguration.LoadAsync()),
        "different encrypted and legacy settings are reported as a migration conflict");
    Check(
        File.ReadAllBytes(secureConfigurationPath).AsSpan().SequenceEqual(secureBeforeConflict) &&
        File.ReadAllBytes(legacyConfigurationPath).AsSpan().SequenceEqual(legacyBeforeConflict),
        "a settings migration conflict preserves both source files");
    Check(
        await ThrowsAsync<InvalidOperationException>(() => conflictConfiguration.SaveAsync()),
        "a settings migration conflict blocks later saves");
    File.Delete(legacyConfigurationPath);

    var canaries = new[] { emailCanary, secondEmailCanary, toolCanary, deviceToolCanary };
    var plaintextCanaryFound = Directory
        .EnumerateFiles(encryptedConfigurationRoot, "*", SearchOption.AllDirectories)
        .Select(File.ReadAllBytes)
        .Any(fileBytes => canaries.Any(canary =>
            fileBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(canary)) >= 0 ||
            fileBytes.AsSpan().IndexOf(Encoding.Unicode.GetBytes(canary)) >= 0));
    Check(!plaintextCanaryFound, "local settings files contain no UTF-8 or UTF-16 plaintext canaries");

    var prohibitedConfigurationTerms = new[]
    {
        "Password",
        "Verification",
        "Passphrase",
        "TwoFactor",
        "Token",
        "Cookie",
        "Session",
        "Credential"
    };
    var secretConfigurationProperties = new[]
        {
            typeof(AppConfiguration),
            typeof(AppleAccountProfile)
        }
        .SelectMany(type => type.GetProperties().Select(property => $"{type.Name}.{property.Name}"))
        .Where(property =>
            prohibitedConfigurationTerms.Any(term =>
                property.Contains(term, StringComparison.OrdinalIgnoreCase)))
        .ToArray();
    Check(
        secretConfigurationProperties.Length == 0,
        "passwords, verification codes, and vault passphrases remain non-persistent");
}
finally
{
    Directory.Delete(encryptedConfigurationRoot, recursive: true);
}

var legacyMigrationRoot = Path.Combine(
    Path.GetTempPath(),
    $"ipa-bridge-legacy-migration-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(legacyMigrationRoot);
try
{
    var secureConfigurationPath = Path.Combine(legacyMigrationRoot, "settings.secure.json");
    var legacyConfigurationPath = Path.Combine(legacyMigrationRoot, "settings.json");
    var keyPath = Path.Combine(legacyMigrationRoot, "master-key.v1");
    var downloadPath = Path.Combine(legacyMigrationRoot, "Downloads");
    const string legacyEmail = "legacy-migration-canary@example.invalid";
    var legacyConfiguration = new
    {
        IpatoolPath = Path.Combine(legacyMigrationRoot, "legacy-ipatool.exe"),
        DeviceToolsDirectory = Path.Combine(legacyMigrationRoot, "legacy-device-tools"),
        DownloadDirectory = downloadPath,
        AppleAccountEmail = legacyEmail,
        AutomaticallyRefreshDevices = false
    };
    await File.WriteAllTextAsync(
        legacyConfigurationPath,
        JsonSerializer.Serialize(legacyConfiguration, new JsonSerializerOptions { WriteIndented = true }));

    var migrationService = new ConfigurationService(
        secureConfigurationPath,
        legacyConfigurationPath,
        keyPath,
        downloadPath,
        new SmokeKeyProtector(),
        new SmokeRandomByteGenerator());
    var migratedConfiguration = await migrationService.LoadAsync();
    Check(
        migratedConfiguration.SchemaVersion == AppConfiguration.CurrentSchemaVersion &&
        migratedConfiguration.AppleAccountEmail is null &&
        migratedConfiguration.AppleAccounts.Count == 1 &&
        migratedConfiguration.AppleAccounts[0].Email == legacyEmail &&
        migratedConfiguration.SelectedAppleAccountId == migratedConfiguration.AppleAccounts[0].Id &&
        migratedConfiguration.IpatoolPath == legacyConfiguration.IpatoolPath &&
        !migratedConfiguration.AutomaticallyRefreshDevices,
        "legacy plaintext settings preserve every value during migration");
    Check(
        File.Exists(secureConfigurationPath) &&
        File.Exists(keyPath) &&
        !File.Exists(legacyConfigurationPath),
        "verified legacy settings migration removes the plaintext file");
    Check(
        !File.ReadAllText(secureConfigurationPath)
            .Contains(legacyEmail, StringComparison.Ordinal),
        "migrated settings are encrypted before legacy plaintext is removed");

    var migratedReopenService = new ConfigurationService(
        secureConfigurationPath,
        legacyConfigurationPath,
        keyPath,
        downloadPath,
        new SmokeKeyProtector(),
        new SmokeRandomByteGenerator());
    var migratedReopened = await migratedReopenService.LoadAsync();
    Check(
        migratedReopened.AppleAccounts.Count == 1 &&
        migratedReopened.AppleAccounts[0].Email == legacyEmail &&
        migratedReopened.AppleAccounts[0].Id == migratedConfiguration.AppleAccounts[0].Id &&
        migratedReopened.SelectedAppleAccountId == migratedConfiguration.SelectedAppleAccountId,
        "migrated encrypted settings reopen with a stable account profile ID");
}
finally
{
    Directory.Delete(legacyMigrationRoot, recursive: true);
}

var encryptedLegacyMigrationRoot = Path.Combine(
    Path.GetTempPath(),
    $"ipa-bridge-encrypted-legacy-migration-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(encryptedLegacyMigrationRoot);
try
{
    var secureConfigurationPath = Path.Combine(
        encryptedLegacyMigrationRoot,
        "settings.secure.json");
    var legacyConfigurationPath = Path.Combine(
        encryptedLegacyMigrationRoot,
        "settings.json");
    var keyPath = Path.Combine(encryptedLegacyMigrationRoot, "master-key.v1");
    var downloadPath = Path.Combine(encryptedLegacyMigrationRoot, "Downloads");
    const string encryptedLegacyEmail = "encrypted-legacy@example.invalid";
    var keyProtector = new SmokeKeyProtector();
    var randomByteGenerator = new SmokeRandomByteGenerator();
    var dataProtectionService = new LocalDataProtectionService(
        keyPath,
        keyProtector,
        randomByteGenerator);
    var legacyPlaintext = JsonSerializer.SerializeToUtf8Bytes(new
    {
        IpatoolPath = string.Empty,
        DeviceToolsDirectory = string.Empty,
        DownloadDirectory = downloadPath,
        AppleAccountEmail = encryptedLegacyEmail,
        AutomaticallyRefreshDevices = true
    });
    var legacyEnvelope = await dataProtectionService.EncryptAsync(
        legacyPlaintext,
        allowKeyCreation: true);
    try
    {
        await File.WriteAllBytesAsync(secureConfigurationPath, legacyEnvelope);
    }
    finally
    {
        CryptographicOperations.ZeroMemory(legacyPlaintext);
        CryptographicOperations.ZeroMemory(legacyEnvelope);
    }

    var encryptedLegacyService = new ConfigurationService(
        secureConfigurationPath,
        legacyConfigurationPath,
        keyPath,
        downloadPath,
        new SmokeKeyProtector(),
        new SmokeRandomByteGenerator());
    var encryptedLegacyMigrated = await encryptedLegacyService.LoadAsync();
    var migratedAccountId = encryptedLegacyMigrated.AppleAccounts.Single().Id;
    Check(
        encryptedLegacyMigrated.AppleAccounts.Single().Email == encryptedLegacyEmail &&
        encryptedLegacyMigrated.SelectedAppleAccountId == migratedAccountId &&
        encryptedLegacyMigrated.AppleAccountEmail is null,
        "legacy encrypted single-account settings migrate to one selected profile");

    var encryptedLegacyReopen = new ConfigurationService(
        secureConfigurationPath,
        legacyConfigurationPath,
        keyPath,
        downloadPath,
        new SmokeKeyProtector(),
        new SmokeRandomByteGenerator());
    Check(
        (await encryptedLegacyReopen.LoadAsync()).AppleAccounts.Single().Id == migratedAccountId,
        "legacy encrypted migration is rewritten with a stable profile ID");
}
finally
{
    Directory.Delete(encryptedLegacyMigrationRoot, recursive: true);
}

var accountNormalizationRoot = Path.Combine(
    Path.GetTempPath(),
    $"ipa-bridge-account-normalization-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(accountNormalizationRoot);
try
{
    var secureConfigurationPath = Path.Combine(accountNormalizationRoot, "settings.secure.json");
    var legacyConfigurationPath = Path.Combine(accountNormalizationRoot, "settings.json");
    var keyPath = Path.Combine(accountNormalizationRoot, "master-key.v1");
    var downloadPath = Path.Combine(accountNormalizationRoot, "Downloads");
    var normalizationService = new ConfigurationService(
        secureConfigurationPath,
        legacyConfigurationPath,
        keyPath,
        downloadPath,
        new SmokeKeyProtector(),
        new SmokeRandomByteGenerator());
    await normalizationService.LoadAsync();
    const string retainedAccountId = "33333333333333333333333333333333";
    const string duplicateAccountId = "44444444444444444444444444444444";
    normalizationService.Current.AppleAccounts =
    [
        new AppleAccountProfile
        {
            Id = retainedAccountId,
            Email = "  First.Account@example.invalid  "
        },
        new AppleAccountProfile
        {
            Id = duplicateAccountId,
            Email = "first.account@EXAMPLE.invalid"
        },
        new AppleAccountProfile
        {
            Id = "55555555555555555555555555555555",
            Email = "   "
        }
    ];
    normalizationService.Current.SelectedAppleAccountId = duplicateAccountId;
    await normalizationService.SaveAsync();
    Check(
        normalizationService.Current.AppleAccounts.Count == 1 &&
        normalizationService.Current.AppleAccounts[0].Id == retainedAccountId &&
        normalizationService.Current.AppleAccounts[0].Email ==
        "First.Account@example.invalid" &&
        normalizationService.Current.SelectedAppleAccountId == retainedAccountId,
        "account profiles trim emails, deduplicate case-insensitively, and repair selection");

    var validNormalizedSettings = File.ReadAllBytes(secureConfigurationPath);
    normalizationService.Current.AppleAccounts =
    [
        new AppleAccountProfile
        {
            Id = "..\\outside",
            Email = "unsafe@example.invalid"
        }
    ];
    Check(
        await ThrowsAsync<InvalidDataException>(() => normalizationService.SaveAsync()),
        "unsafe account profile IDs are rejected before session paths are used");
    Check(
        File.ReadAllBytes(secureConfigurationPath).AsSpan().SequenceEqual(validNormalizedSettings),
        "rejected account profile IDs do not overwrite encrypted settings");
}
finally
{
    Directory.Delete(accountNormalizationRoot, recursive: true);
}

var unknownLegacyFieldRoot = Path.Combine(
    Path.GetTempPath(),
    $"ipa-bridge-unknown-legacy-field-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(unknownLegacyFieldRoot);
try
{
    var secureConfigurationPath = Path.Combine(unknownLegacyFieldRoot, "settings.secure.json");
    var legacyConfigurationPath = Path.Combine(unknownLegacyFieldRoot, "settings.json");
    var keyPath = Path.Combine(unknownLegacyFieldRoot, "master-key.v1");
    var downloadPath = Path.Combine(unknownLegacyFieldRoot, "Downloads");
    const string newerLegacySettings =
        """
        {
          "DownloadDirectory": "C:\\Downloads",
          "FutureSensitiveSetting": "preserve-this-value"
        }
        """;
    await File.WriteAllTextAsync(legacyConfigurationPath, newerLegacySettings);
    var unknownFieldConfiguration = new ConfigurationService(
        secureConfigurationPath,
        legacyConfigurationPath,
        keyPath,
        downloadPath,
        new SmokeKeyProtector(),
        new SmokeRandomByteGenerator());
    Check(
        await ThrowsAsync<InvalidDataException>(() => unknownFieldConfiguration.LoadAsync()),
        "unknown legacy settings fields are rejected instead of discarded");
    Check(
        File.ReadAllText(legacyConfigurationPath) == newerLegacySettings &&
        !File.Exists(secureConfigurationPath),
        "a newer legacy settings schema is preserved without migration");
}
finally
{
    Directory.Delete(unknownLegacyFieldRoot, recursive: true);
}

var failedMigrationRoot = Path.Combine(
    Path.GetTempPath(),
    $"ipa-bridge-failed-migration-smoke-{Guid.NewGuid():N}");
Directory.CreateDirectory(failedMigrationRoot);
try
{
    var blockedSecurePath = Path.Combine(failedMigrationRoot, "settings.secure.json");
    var legacyConfigurationPath = Path.Combine(failedMigrationRoot, "settings.json");
    var keyPath = Path.Combine(failedMigrationRoot, "master-key.v1");
    var downloadPath = Path.Combine(failedMigrationRoot, "Downloads");
    Directory.CreateDirectory(blockedSecurePath);
    await File.WriteAllTextAsync(
        legacyConfigurationPath,
        JsonSerializer.Serialize(new AppConfiguration
        {
            AppleAccountEmail = "preserve-on-failure@example.invalid",
            DownloadDirectory = downloadPath
        }));

    var failedMigrationService = new ConfigurationService(
        blockedSecurePath,
        legacyConfigurationPath,
        keyPath,
        downloadPath,
        new SmokeKeyProtector(),
        new SmokeRandomByteGenerator());
    var migrationCommitFailed = false;
    try
    {
        await failedMigrationService.LoadAsync();
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        migrationCommitFailed = true;
    }

    Check(migrationCommitFailed, "legacy migration surfaces an encrypted settings commit failure");
    Check(
        File.Exists(legacyConfigurationPath) &&
        File.ReadAllText(legacyConfigurationPath)
            .Contains("preserve-on-failure@example.invalid", StringComparison.Ordinal),
        "failed legacy migration preserves the original plaintext configuration");
    Check(
        !Directory.EnumerateFiles(
                failedMigrationRoot,
                "settings.secure.json.*.tmp",
                SearchOption.TopDirectoryOnly)
            .Any(),
        "failed legacy migration removes its encrypted temporary file");
}
finally
{
    Directory.Delete(failedMigrationRoot, recursive: true);
}

var productionKeyProtector = new WindowsCurrentUserKeyProtector();
var productionRawKey = RandomNumberGenerator.GetBytes(LocalDataProtectionService.MasterKeySize);
var productionProtectedKey = productionKeyProtector.Protect(productionRawKey);
var productionUnprotectedKey = productionKeyProtector.Unprotect(productionProtectedKey);
try
{
    Check(
        !productionProtectedKey.AsSpan().SequenceEqual(productionRawKey),
        "Windows CurrentUser protection does not persist the raw settings key");
    Check(
        productionUnprotectedKey.AsSpan().SequenceEqual(productionRawKey),
        "Windows CurrentUser protection unlocks the settings key for the same user");
}
finally
{
    CryptographicOperations.ZeroMemory(productionRawKey);
    CryptographicOperations.ZeroMemory(productionProtectedKey);
    CryptographicOperations.ZeroMemory(productionUnprotectedKey);
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

    var conPtyAccountHome = Path.Combine(
        Path.GetTempPath(),
        $"ipa-bridge-conpty-account-home-{Guid.NewGuid():N}");
    var accountEnvironment = IpatoolService.BuildAccountEnvironment(conPtyAccountHome)
        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    accountEnvironment["IPA_BRIDGE_ACCOUNT_ENVIRONMENT_SMOKE"] = "isolated-account-session";
    var environmentResult = await runner.RunAsync(
        executable,
        ["--environment-child"],
        environment: accountEnvironment);
    Check(
        environmentResult.IsSuccess &&
        environmentResult.Output.Contains(
            "marker=isolated-account-session",
            StringComparison.Ordinal) &&
        environmentResult.Output.Contains(
            $"drive={accountEnvironment["HOMEDRIVE"]}",
            StringComparison.Ordinal) &&
        environmentResult.Output.Contains(
            $"path={accountEnvironment["HOMEPATH"]}",
            StringComparison.Ordinal),
        "ConPTY applies an isolated per-account Windows home environment");

    var temporaryDownloads = Path.Combine(
        Path.GetTempPath(),
        $"ipa-bridge-download-smoke-{Guid.NewGuid():N}");
    var temporaryAccountSessions = Path.Combine(
        Path.GetTempPath(),
        $"ipa-bridge-account-session-smoke-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temporaryDownloads);
    var originalFakeIpatoolMode = Environment.GetEnvironmentVariable(
        "IPA_BRIDGE_SMOKE_FAKE_IPATOOL");
    var originalLoginWaitMarker = Environment.GetEnvironmentVariable(
        "IPA_BRIDGE_SMOKE_LOGIN_WAIT_MARKER");
    var loginWaitMarker = Path.Combine(
        Path.GetTempPath(),
        $"ipa-bridge-login-wait-{Guid.NewGuid():N}.txt");
    var fakeAccountId = Guid.NewGuid().ToString("N");
    try
    {
        var fakeConfiguration = new ConfigurationService();
        fakeConfiguration.Current.IpatoolPath = executable;
        var fakeIpatool = new IpatoolService(
            new ToolLocationService(fakeConfiguration),
            new ProcessRunner(),
            new ConPtyProcessRunner(),
            temporaryAccountSessions);

        Environment.SetEnvironmentVariable(
            "IPA_BRIDGE_SMOKE_FAKE_IPATOOL",
            "login-wait");
        Environment.SetEnvironmentVariable(
            "IPA_BRIDGE_SMOKE_LOGIN_WAIT_MARKER",
            loginWaitMarker);
        var cancelableStore = new StoreViewModel(
            fakeConfiguration,
            fakeIpatool,
            (_, _, _) => { });
        cancelableStore.AddAccountCommand.Execute(null);
        cancelableStore.Email = "cancel@example.invalid";
        cancelableStore.ApplePassword = "temporary-apple-secret";
        cancelableStore.VaultPassphrase = "temporary-vault-secret";
        var loginTask = cancelableStore.LoginCommand.ExecuteAsync();
        var markerDeadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(loginWaitMarker) && DateTime.UtcNow < markerDeadline)
        {
            await Task.Delay(25);
        }

        Check(
            File.Exists(loginWaitMarker) && cancelableStore.IsBusy,
            "an in-flight Apple Account login reaches the cancellable isolated session");
        var pendingAccountDirectory = Directory
            .EnumerateDirectories(temporaryAccountSessions)
            .Single();
        var lockedCookie = Path.Combine(pendingAccountDirectory, "cleanup.lock");
        await using (var lockedCookieStream = new FileStream(
                         lockedCookie,
                         FileMode.CreateNew,
                         FileAccess.ReadWrite,
                         FileShare.None))
        {
            cancelableStore.LeaveStore();
            await loginTask.WaitAsync(TimeSpan.FromSeconds(10));
            Check(
                !cancelableStore.IsBusy &&
                cancelableStore.IsAddingAccount &&
                Directory.Exists(pendingAccountDirectory),
                "failed pending-session cleanup retains a retryable account handle");
        }

        cancelableStore.CancelAccountEditCommand.Execute(null);
        Check(
            !cancelableStore.IsAddingAccount &&
            !Directory.Exists(pendingAccountDirectory),
            "pending-session cleanup succeeds on retry after the file lock is released");

        var fakeApp = new StoreApp
        {
            BundleIdentifier = "com.example.cleanup",
            Name = "Cleanup Test"
        };
        var fakeAccount = new AppleAccountProfile
        {
            Id = fakeAccountId,
            Email = "cleanup@example.invalid"
        };

        Environment.SetEnvironmentVariable(
            "IPA_BRIDGE_SMOKE_FAKE_IPATOOL",
            "download-failure");
        var downloadFailed = false;
        try
        {
            await fakeIpatool.DownloadAsync(
                fakeAccount,
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

        var secondFakeAccount = new AppleAccountProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Email = "cleanup@example.invalid"
        };
        _ = await fakeIpatool.GetStoredAccountAsync(
            secondFakeAccount,
            "temporary-vault-secret");
        Check(
            Directory.Exists(Path.Combine(temporaryAccountSessions, fakeAccount.Id)) &&
            Directory.Exists(Path.Combine(temporaryAccountSessions, secondFakeAccount.Id)) &&
            !string.Equals(fakeAccount.Id, secondFakeAccount.Id, StringComparison.Ordinal),
            "two Apple Account profiles receive distinct local ipatool home directories");

        Environment.SetEnvironmentVariable(
            "IPA_BRIDGE_SMOKE_FAKE_IPATOOL",
            "search-success");
        var fakeSearchResults = await fakeIpatool.SearchAsync(
            fakeAccount,
            "bridge",
            "temporary-vault-secret");
        Check(
            fakeSearchResults.Count == 1 &&
            fakeSearchResults[0].BundleIdentifier == "com.example.bridge",
            "search uses the pinned v2.3.0 flags inside the selected account environment");

        var mismatchedAccount = new AppleAccountProfile
        {
            Id = Guid.NewGuid().ToString("N"),
            Email = "different@example.invalid"
        };
        var mismatchedSessionRejected = false;
        try
        {
            _ = await fakeIpatool.SearchAsync(
                mismatchedAccount,
                "bridge",
                "temporary-vault-secret");
        }
        catch (IpatoolAccountSessionException exception)
            when (exception.Message.Contains(
                "contains cleanup@example.invalid",
                StringComparison.Ordinal))
        {
            mismatchedSessionRejected = true;
        }

        Check(
            mismatchedSessionRejected,
            "a selected profile cannot use another account's isolated ipatool session");

        Environment.SetEnvironmentVariable(
            "IPA_BRIDGE_SMOKE_FAKE_IPATOOL",
            "download-success");
        var completedIpa = await fakeIpatool.DownloadAsync(
            fakeAccount,
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
        Environment.SetEnvironmentVariable(
            "IPA_BRIDGE_SMOKE_LOGIN_WAIT_MARKER",
            originalLoginWaitMarker);
        File.Delete(loginWaitMarker);
        Directory.Delete(temporaryDownloads, recursive: true);
        if (Directory.Exists(temporaryAccountSessions))
        {
            Directory.Delete(temporaryAccountSessions, recursive: true);
        }
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

            var searchHelpResult = await processRunner.RunAsync(
                officialIpatool,
                ["search", "--help"],
                cancellationToken: offlineContractTimeout.Token);
            Check(
                searchHelpResult.IsSuccess &&
                searchHelpResult.CombinedOutput.Contains("--limit", StringComparison.Ordinal) &&
                !searchHelpResult.CombinedOutput.Contains("--platform", StringComparison.Ordinal),
                "pinned ipatool search flags match IPA Bridge arguments");

            var downloadHelpResult = await processRunner.RunAsync(
                officialIpatool,
                ["download", "--help"],
                cancellationToken: offlineContractTimeout.Token);
            Check(
                downloadHelpResult.IsSuccess &&
                downloadHelpResult.CombinedOutput.Contains("--purchase", StringComparison.Ordinal) &&
                !downloadHelpResult.CombinedOutput.Contains("--platform", StringComparison.Ordinal),
                "pinned ipatool download flags match IPA Bridge arguments");

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

internal sealed class SmokeKeyProtector : ILocalDataKeyProtector
{
    public byte[]? LastProtectedPlaintext { get; private set; }

    public byte[] Protect(byte[] data)
    {
        LastProtectedPlaintext = data.ToArray();
        return data
            .Reverse()
            .Select(value => (byte)(value ^ 0xA5))
            .ToArray();
    }

    public byte[] Unprotect(byte[] protectedData)
    {
        return protectedData
            .Select(value => (byte)(value ^ 0xA5))
            .Reverse()
            .ToArray();
    }
}

internal sealed class SmokeRandomByteGenerator : IRandomByteGenerator
{
    private int _requestCount;

    public int RequestCount => _requestCount;

    public byte[] GetBytes(int count)
    {
        var request = Interlocked.Increment(ref _requestCount);
        return Enumerable.Range(0, count)
            .Select(index => (byte)((index * 37 + request * 19) & 0xFF))
            .ToArray();
    }
}
