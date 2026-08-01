using System.Diagnostics;
using System.Runtime.InteropServices;
using IPABridge.Infrastructure;
using IPABridge.Models;
using IPABridge.Services;

namespace IPABridge.ViewModels;

public sealed class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly ConfigurationService _configurationService;
    private readonly ToolLocationService _toolLocationService;
    private readonly ToolBootstrapService _toolBootstrapService;
    private readonly IpatoolService _ipatoolService;
    private readonly SystemPrerequisiteService _prerequisiteService;
    private readonly DeviceEnvironmentOperationGate _deviceEnvironmentGate;
    private readonly Action<string, string, bool> _addActivity;
    private readonly ApplicationUpdateService _applicationUpdateService;
    private string _ipatoolStatus = "Checking";
    private string _deviceToolsStatus = "Checking";
    private string _installationStage = string.Empty;
    private double _installationProgress;
    private bool _isInstalling;
    private bool _isInstallingIpatool;
    private string _ipatoolInstallationMessage =
        "You can also choose an existing ipatool.exe in Settings.";
    private string _statusMessage =
        "Settings and generated profile keys are encrypted locally. Apple passwords and verification codes are not saved.";
    private string _updateStatusMessage;
    private string _latestReleaseUrl = string.Empty;
    private bool _isCheckingForUpdates;
    private bool _hasReleaseInformation;
    private bool _isUpdateAvailable;

    public SettingsViewModel(
        ConfigurationService configurationService,
        ToolLocationService toolLocationService,
        ToolBootstrapService toolBootstrapService,
        IpatoolService ipatoolService,
        SystemPrerequisiteService prerequisiteService,
        DeviceEnvironmentOperationGate deviceEnvironmentGate,
        Action<string, string, bool> addActivity,
        ApplicationUpdateService? applicationUpdateService = null)
    {
        _configurationService = configurationService;
        _toolLocationService = toolLocationService;
        _toolBootstrapService = toolBootstrapService;
        _ipatoolService = ipatoolService;
        _prerequisiteService = prerequisiteService;
        _deviceEnvironmentGate = deviceEnvironmentGate;
        _addActivity = addActivity;
        _applicationUpdateService = applicationUpdateService ?? new ApplicationUpdateService();
        _updateStatusMessage =
            $"{_applicationUpdateService.CurrentBuildLabel}. Check GitHub for the newest published executable.";

        InstallIpatoolCommand = new AsyncRelayCommand(
            InstallIpatoolAsync,
            () => !IsInstalling,
            exception => ReportError("Could not install ipatool", exception));
        InstallDeviceToolsCommand = new AsyncRelayCommand(
            InstallDeviceToolsAsync,
            () => !IsInstalling && !_deviceEnvironmentGate.IsBusy,
            exception => ReportError("Could not install the iOS device tools", exception));
        InstallAppleDevicesCommand = new AsyncRelayCommand(
            InstallAppleDevicesAsync,
            () => !IsInstalling && !_deviceEnvironmentGate.IsBusy,
            exception => ReportError("Could not install Apple Devices", exception));
        RefreshAppleDeviceSupportCommand = new AsyncRelayCommand(
            RefreshAppleDeviceSupportAsync,
            () => !IsInstalling && !_deviceEnvironmentGate.IsBusy,
            exception => ReportError("Could not refresh Apple device support", exception));
        SaveCommand = new AsyncRelayCommand(
            SaveAsync,
            exceptionHandler: exception => ReportError("Could not save settings", exception));
        CheckForUpdatesCommand = new AsyncRelayCommand(
            CheckForUpdatesAsync,
            () => !IsCheckingForUpdates,
            exception => ReportUpdateError(exception));
        OpenLatestReleaseCommand = new RelayCommand(
            () => OpenUri(LatestReleaseUrl),
            () => HasReleaseInformation);
        OpenAppleDevicesHelpCommand = new RelayCommand(() =>
            OpenUri("https://support.apple.com/guide/devices-windows/install-the-apple-devices-app-mchl5ded2763/windows"));
        OpenIpatoolSourceCommand = new RelayCommand(() =>
            OpenUri("https://github.com/majd/ipatool"));
        OpenDeviceToolsSourceCommand = new RelayCommand(() =>
            OpenUri("https://github.com/jkcoxson/idevice"));
        OpenAppleDevicesStoreCommand = new RelayCommand(
            SystemPrerequisiteService.OpenAppleDevicesStorePage);

        _deviceEnvironmentGate.StateChanged += DeviceEnvironmentGateOnStateChanged;
    }

    public event EventHandler? ToolsChanged;

    public event Action<AppleDeviceSupportStatus>? AppleDeviceSupportChanged;

    public AsyncRelayCommand InstallIpatoolCommand { get; }

    public AsyncRelayCommand InstallDeviceToolsCommand { get; }

    public AsyncRelayCommand InstallAppleDevicesCommand { get; }

    public AsyncRelayCommand RefreshAppleDeviceSupportCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand CheckForUpdatesCommand { get; }

    public RelayCommand OpenLatestReleaseCommand { get; }

    public RelayCommand OpenAppleDevicesHelpCommand { get; }

    public RelayCommand OpenIpatoolSourceCommand { get; }

    public RelayCommand OpenDeviceToolsSourceCommand { get; }

    public RelayCommand OpenAppleDevicesStoreCommand { get; }

    public string IpatoolPath
    {
        get => _configurationService.Current.IpatoolPath;
        set
        {
            if (string.Equals(_configurationService.Current.IpatoolPath, value, StringComparison.Ordinal))
            {
                return;
            }

            _configurationService.Current.IpatoolPath = value;
            OnPropertyChanged();
        }
    }

    public string DeviceToolsDirectory
    {
        get => _configurationService.Current.DeviceToolsDirectory;
        set
        {
            if (string.Equals(
                    _configurationService.Current.DeviceToolsDirectory,
                    value,
                    StringComparison.Ordinal))
            {
                return;
            }

            _configurationService.Current.DeviceToolsDirectory = value;
            OnPropertyChanged();
        }
    }

    public string DownloadDirectory
    {
        get => _configurationService.Current.DownloadDirectory;
        set
        {
            if (string.Equals(_configurationService.Current.DownloadDirectory, value, StringComparison.Ordinal))
            {
                return;
            }

            _configurationService.Current.DownloadDirectory = value;
            OnPropertyChanged();
        }
    }

    public bool AutomaticallyRefreshDevices
    {
        get => _configurationService.Current.AutomaticallyRefreshDevices;
        set
        {
            if (_configurationService.Current.AutomaticallyRefreshDevices == value)
            {
                return;
            }

            _configurationService.Current.AutomaticallyRefreshDevices = value;
            OnPropertyChanged();
        }
    }

    public string IpatoolStatus
    {
        get => _ipatoolStatus;
        private set => SetProperty(ref _ipatoolStatus, value);
    }

    public string DeviceToolsStatus
    {
        get => _deviceToolsStatus;
        private set => SetProperty(ref _deviceToolsStatus, value);
    }

    public string InstallationStage
    {
        get => _installationStage;
        private set => SetProperty(ref _installationStage, value);
    }

    public double InstallationProgress
    {
        get => _installationProgress;
        private set => SetProperty(ref _installationProgress, value);
    }

    public bool IsInstalling
    {
        get => _isInstalling;
        private set
        {
            if (!SetProperty(ref _isInstalling, value))
            {
                return;
            }

            InstallIpatoolCommand.NotifyCanExecuteChanged();
            InstallDeviceToolsCommand.NotifyCanExecuteChanged();
            InstallAppleDevicesCommand.NotifyCanExecuteChanged();
            RefreshAppleDeviceSupportCommand.NotifyCanExecuteChanged();
        }
    }

    public bool IsInstallingIpatool
    {
        get => _isInstallingIpatool;
        private set => SetProperty(ref _isInstallingIpatool, value);
    }

    public string IpatoolInstallationMessage
    {
        get => _ipatoolInstallationMessage;
        private set => SetProperty(ref _ipatoolInstallationMessage, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string CurrentBuildLabel => _applicationUpdateService.CurrentBuildLabel;

    public string UpdateStatusMessage
    {
        get => _updateStatusMessage;
        private set => SetProperty(ref _updateStatusMessage, value);
    }

    public string LatestReleaseUrl
    {
        get => _latestReleaseUrl;
        private set => SetProperty(ref _latestReleaseUrl, value);
    }

    public bool IsCheckingForUpdates
    {
        get => _isCheckingForUpdates;
        private set
        {
            if (SetProperty(ref _isCheckingForUpdates, value))
            {
                CheckForUpdatesCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasReleaseInformation
    {
        get => _hasReleaseInformation;
        private set
        {
            if (SetProperty(ref _hasReleaseInformation, value))
            {
                OpenLatestReleaseCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        private set => SetProperty(ref _isUpdateAvailable, value);
    }

    public bool IsIpatoolAvailable => _toolLocationService.ResolveIpatool() is not null;

    public bool AreDeviceToolsAvailable => _toolLocationService.ResolveDeviceTools().IsAvailable;

    public async Task RefreshStatusAsync()
    {
        var version = await _ipatoolService.GetVersionAsync();
        IpatoolStatus = BuildIpatoolStatus(version);

        var deviceTools = _toolLocationService.ResolveDeviceTools();
        DeviceToolsStatus = deviceTools.Backend switch
        {
            DeviceBackend.ModernIdeviceTools => "Configured — idevice-tools",
            DeviceBackend.Libimobiledevice => "Configured — libimobiledevice",
            _ => "Not installed"
        };

        OnPropertyChanged(nameof(IsIpatoolAvailable));
        OnPropertyChanged(nameof(AreDeviceToolsAvailable));
    }

    internal static string BuildIpatoolStatus(string? installedVersion)
    {
        if (string.IsNullOrWhiteSpace(installedVersion))
        {
            return "Not installed";
        }

        var pinnedVersion = ToolBootstrapService
            .GetPinnedIpatoolPackage(RuntimeInformation.ProcessArchitecture)
            .Version
            .TrimStart('v');
        var normalizedInstalledVersion = installedVersion.Trim().TrimStart('v');
        return string.Equals(
            normalizedInstalledVersion,
            pinnedVersion,
            StringComparison.OrdinalIgnoreCase)
            ? $"Ready — {normalizedInstalledVersion}"
            : $"Update available — {normalizedInstalledVersion} → {pinnedVersion}";
    }

    public async Task ApplySelectedPathsAsync()
    {
        await SaveAsync();
        await RefreshStatusAsync();
        ToolsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ReportError(string operation, Exception exception)
    {
        StatusMessage = $"{operation}: {exception.Message}";
        try
        {
            _addActivity(operation, exception.Message, false);
        }
        catch
        {
            // The local status remains available even if the activity feed is shutting down.
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        IsCheckingForUpdates = true;
        UpdateStatusMessage = "Checking GitHub Releases…";
        try
        {
            var result = await _applicationUpdateService.CheckAsync();
            LatestReleaseUrl = result.ReleaseUrl;
            HasReleaseInformation = true;
            IsUpdateAvailable = result.IsUpdateAvailable;
            UpdateStatusMessage = !result.CanCompareBuilds
                ? $"Latest release: {result.ReleaseName}, published {result.PublishedAt:yyyy-MM-dd}. " +
                  "This development build has no release revision to compare."
                : result.IsUpdateAvailable
                    ? $"Update available: {result.ReleaseName}, published {result.PublishedAt:yyyy-MM-dd}."
                    : $"IPA Bridge is up to date: {result.ReleaseName}.";
            _addActivity(
                "Software update check",
                UpdateStatusMessage,
                !result.IsUpdateAvailable);
        }
        finally
        {
            IsCheckingForUpdates = false;
        }
    }

    private void ReportUpdateError(Exception exception)
    {
        LatestReleaseUrl = string.Empty;
        HasReleaseInformation = false;
        IsUpdateAvailable = false;
        UpdateStatusMessage = $"Could not check GitHub Releases: {exception.Message}";
        _addActivity("Software update check failed", exception.Message, false);
    }

    private async Task InstallIpatoolAsync()
    {
        IsInstallingIpatool = true;
        InstallationStage = "Preparing the official ipatool download…";
        IpatoolInstallationMessage =
            "IPA Bridge downloads the reviewed official release and verifies its SHA-256 before use.";
        try
        {
            if (await RunInstallationAsync(
                    "Install ipatool",
                    progress => _toolBootstrapService.InstallIpatoolAsync(progress),
                    "The official ipatool was installed and passed SHA-256 verification."))
            {
                IpatoolInstallationMessage =
                    "ipatool is ready. Enter your Apple Account details to continue.";
                ToolsChanged?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                var reason = StatusMessage.Trim().TrimEnd('.');
                IpatoolInstallationMessage =
                    $"ipatool could not be installed. {reason}. " +
                    "Try again or choose an existing ipatool.exe in Settings.";
            }
        }
        finally
        {
            IsInstallingIpatool = false;
        }
    }

    private async Task InstallDeviceToolsAsync()
    {
        bool succeeded;
        using (var operation = _deviceEnvironmentGate.TryEnter())
        {
            if (operation is null)
            {
                return;
            }

            succeeded = await RunInstallationAsync(
                "Install iOS device tools",
                progress => _toolBootstrapService.InstallIdeviceToolsAsync(progress),
                "The iOS device tools were installed and passed pinned SHA-256 verification.");
        }

        if (succeeded)
        {
            ToolsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task InstallAppleDevicesAsync()
    {
        using var operation = _deviceEnvironmentGate.TryEnter();
        if (operation is null)
        {
            return;
        }

        const string title = "Get Apple Devices";
        IsInstalling = true;
        InstallationProgress = 0;
        try
        {
            var progress = new Progress<ToolInstallationProgress>(value =>
            {
                InstallationStage = value.Stage;
                InstallationProgress = value.Percentage;
            });
            var result = await _prerequisiteService.InstallAppleDevicesAsync(progress);
            InstallationProgress = 1;
            StatusMessage = result.Message;
            AppleDeviceSupportChanged?.Invoke(result.Status);

            _addActivity(title, result.Message, result.AutomaticInstallationVerified);
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            _addActivity(title, exception.Message, false);
        }
        finally
        {
            IsInstalling = false;
        }
    }

    private async Task RefreshAppleDeviceSupportAsync()
    {
        using var operation = _deviceEnvironmentGate.TryEnter();
        if (operation is null)
        {
            return;
        }

        try
        {
            var appleDeviceSupport = await _prerequisiteService.GetAppleDeviceSupportStatusAsync();
            StatusMessage = $"Apple device support: {appleDeviceSupport.OverallLabel}.";
            AppleDeviceSupportChanged?.Invoke(appleDeviceSupport);
        }
        catch (Exception exception)
        {
            StatusMessage = $"Failed to detect Apple device drivers: {exception.Message}";
            _addActivity("Apple device driver detection failed", exception.Message, false);
        }
    }

    private async Task<bool> RunInstallationAsync(
        string title,
        Func<IProgress<ToolInstallationProgress>, Task<string>> operation,
        string? successMessage = null)
    {
        IsInstalling = true;
        InstallationProgress = 0;
        try
        {
            var progress = new Progress<ToolInstallationProgress>(value =>
            {
                InstallationStage = value.Stage;
                InstallationProgress = value.Percentage;
            });
            var operationMessage = await operation(progress);
            var finalMessage = successMessage ?? operationMessage;
            StatusMessage = finalMessage;
            _addActivity(title, finalMessage, true);
            await RefreshStatusAsync();
            OnPropertyChanged(nameof(IpatoolPath));
            OnPropertyChanged(nameof(DeviceToolsDirectory));
            return true;
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            _addActivity(title, exception.Message, false);
            return false;
        }
        finally
        {
            IsInstalling = false;
        }
    }

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(DownloadDirectory))
        {
            DownloadDirectory = AppPaths.DefaultDownloadDirectory;
        }

        Directory.CreateDirectory(DownloadDirectory);
        await _configurationService.SaveAsync();
        StatusMessage = "Settings saved. Sensitive credentials are never written to the configuration.";
        _addActivity("Settings saved", "Paths and preferences updated", true);
        ToolsChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void OpenUri(string uri)
    {
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    private void DeviceEnvironmentGateOnStateChanged(object? sender, EventArgs eventArgs)
    {
        InstallDeviceToolsCommand.NotifyCanExecuteChanged();
        InstallAppleDevicesCommand.NotifyCanExecuteChanged();
        RefreshAppleDeviceSupportCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        _deviceEnvironmentGate.StateChanged -= DeviceEnvironmentGateOnStateChanged;
    }
}
