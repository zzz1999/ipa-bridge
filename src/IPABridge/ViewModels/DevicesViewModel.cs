using System.Collections.ObjectModel;
using System.Windows.Threading;
using IPABridge.Infrastructure;
using IPABridge.Models;
using IPABridge.Services;

namespace IPABridge.ViewModels;

public sealed class DevicesViewModel : ObservableObject, IDisposable
{
    private readonly ConfigurationService _configurationService;
    private readonly DeviceService _deviceService;
    private readonly SystemPrerequisiteService _prerequisiteService;
    private readonly DeviceEnvironmentOperationGate _deviceEnvironmentGate;
    private readonly Action<string, string, bool> _addActivity;
    private readonly DispatcherTimer _refreshTimer;
    private ConnectedDevice? _selectedDevice;
    private string _selectedIpaPath = string.Empty;
    private bool _hasAppleDeviceSupport;
    private AppleDeviceSupportStatus _appleDeviceSupport = AppleDeviceSupportStatus.Checking;
    private bool _isRefreshing;
    private bool _isInstalling;
    private double _installationProgress;
    private string _statusMessage = "Connect a device, then select Refresh.";
    private string _installationLog = string.Empty;
    private CancellationTokenSource? _installationCancellation;

    public DevicesViewModel(
        ConfigurationService configurationService,
        DeviceService deviceService,
        SystemPrerequisiteService prerequisiteService,
        DeviceEnvironmentOperationGate deviceEnvironmentGate,
        Action<string, string, bool> addActivity)
    {
        _configurationService = configurationService;
        _deviceService = deviceService;
        _prerequisiteService = prerequisiteService;
        _deviceEnvironmentGate = deviceEnvironmentGate;
        _addActivity = addActivity;

        RefreshCommand = new AsyncRelayCommand(() => RefreshAsync(), CanRefresh);
        PairCommand = new AsyncRelayCommand(PairAsync, CanPair);
        InstallCommand = new AsyncRelayCommand(InstallAsync, CanInstall);
        CancelInstallCommand = new RelayCommand(CancelInstall, () => IsInstalling);

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(8)
        };
        _refreshTimer.Tick += async (_, _) =>
        {
            if (_configurationService.Current.AutomaticallyRefreshDevices &&
                HasAppleDeviceSupport &&
                _deviceService.ToolLocation.IsAvailable &&
                !IsInstalling &&
                !_deviceEnvironmentGate.IsBusy)
            {
                await RefreshAsync(silent: true);
            }
        };

        _deviceEnvironmentGate.StateChanged += DeviceEnvironmentGateOnStateChanged;
    }

    public ObservableCollection<ConnectedDevice> Items { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand PairCommand { get; }

    public AsyncRelayCommand InstallCommand { get; }

    public RelayCommand CancelInstallCommand { get; }

    public ConnectedDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (!SetProperty(ref _selectedDevice, value))
            {
                return;
            }

            PairCommand.NotifyCanExecuteChanged();
            InstallCommand.NotifyCanExecuteChanged();
        }
    }

    public string SelectedIpaPath
    {
        get => _selectedIpaPath;
        set
        {
            if (SetProperty(ref _selectedIpaPath, value))
            {
                InstallCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasAppleDeviceSupport
    {
        get => _hasAppleDeviceSupport;
        private set
        {
            if (SetProperty(ref _hasAppleDeviceSupport, value))
            {
                PairCommand.NotifyCanExecuteChanged();
                InstallCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public AppleDeviceSupportStatus AppleDeviceSupport
    {
        get => _appleDeviceSupport;
        private set
        {
            if (!SetProperty(ref _appleDeviceSupport, value))
            {
                return;
            }

            HasAppleDeviceSupport = value.IsReady;
            OnPropertyChanged(nameof(AppleDeviceSupportDetail));
        }
    }

    public string AppleDeviceSupportDetail
    {
        get
        {
            if (!AppleDeviceSupport.HasBeenChecked)
            {
                return "Checking Apple device support…";
            }

            if (AppleDeviceSupport.IsReady)
            {
                return AppleDeviceSupport.IsUsbDriverInstalled switch
                {
                    true =>
                        $"The {FormatBackendName(AppleDeviceSupport.BackendName)} idevice_id probe and Apple USB driver{FormatVersion(AppleDeviceSupport.UsbDriverVersion)} are ready.",
                    false =>
                        $"The {FormatBackendName(AppleDeviceSupport.BackendName)} idevice_id probe succeeded, but no Apple USB driver was found in the Windows Driver Store. USB connections may not work.",
                    _ =>
                        $"The {FormatBackendName(AppleDeviceSupport.BackendName)} idevice_id probe succeeded, but the Windows driver inventory could not be read."
                };
            }

            if (AppleDeviceSupport.IsBackendProbeSuccessful is null)
            {
                return "Install or select the iOS device tools to run the required idevice_id communication probe.";
            }

            var detectedComponents = AppleDeviceSupport.IsAppleDevicesInstalled ||
                                     AppleDeviceSupport.IsUsbDriverInstalled == true
                ? "Apple device components were detected, but"
                : "Apple device support is not operational because";
            var error = string.IsNullOrWhiteSpace(AppleDeviceSupport.BackendProbeError)
                ? string.Empty
                : $" ({AppleDeviceSupport.BackendProbeError})";
            var endpointHint = AppleDeviceSupport.IsTransportEndpointReachable
                ? " The local Apple endpoint is reachable, but that diagnostic alone does not establish readiness."
                : string.Empty;
            return $"{detectedComponents} the {FormatBackendName(AppleDeviceSupport.BackendName)} idevice_id probe failed{error}.{endpointHint}";
        }
    }

    public bool AreDeviceToolsAvailable => _deviceService.ToolLocation.IsAvailable;

    public string DeviceToolsLabel => _deviceService.ToolLocation.Backend switch
    {
        DeviceBackend.ModernIdeviceTools => "idevice-tools",
        DeviceBackend.Libimobiledevice => "libimobiledevice",
        _ => "Not configured"
    };

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (!SetProperty(ref _isRefreshing, value))
            {
                return;
            }

            RefreshCommand.NotifyCanExecuteChanged();
            PairCommand.NotifyCanExecuteChanged();
            InstallCommand.NotifyCanExecuteChanged();
        }
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

            RefreshCommand.NotifyCanExecuteChanged();
            PairCommand.NotifyCanExecuteChanged();
            InstallCommand.NotifyCanExecuteChanged();
            CancelInstallCommand.NotifyCanExecuteChanged();
        }
    }

    public double InstallationProgress
    {
        get => _installationProgress;
        private set => SetProperty(ref _installationProgress, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string InstallationLog
    {
        get => _installationLog;
        private set => SetProperty(ref _installationLog, value);
    }

    public async Task InitializeAsync()
    {
        await RefreshPrerequisitesAsync();
        if (_configurationService.Current.AutomaticallyRefreshDevices)
        {
            _refreshTimer.Start();
        }

        if (HasAppleDeviceSupport && AreDeviceToolsAvailable)
        {
            await RefreshAsync(silent: true);
        }
    }

    public async Task RefreshPrerequisitesAsync()
    {
        using var operation = _deviceEnvironmentGate.TryEnter();
        if (operation is null)
        {
            return;
        }

        await RefreshPrerequisitesCoreAsync();
    }

    public void ApplyAppleDeviceSupportStatus(AppleDeviceSupportStatus status)
    {
        AppleDeviceSupport = status;
        if (_configurationService.Current.AutomaticallyRefreshDevices)
        {
            _refreshTimer.Start();
        }
        else
        {
            _refreshTimer.Stop();
        }

        OnPropertyChanged(nameof(AreDeviceToolsAvailable));
        OnPropertyChanged(nameof(DeviceToolsLabel));
        PairCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
        if (!HasAppleDeviceSupport || !AreDeviceToolsAvailable)
        {
            ClearDevices();
        }

        if (!HasAppleDeviceSupport)
        {
            StatusMessage = AppleDeviceSupportDetail;
        }
        else if (!AreDeviceToolsAvailable)
        {
            StatusMessage = "The Apple driver is ready; the iOS device tools still need to be installed.";
        }
        else
        {
            StatusMessage = AppleDeviceSupportDetail;
        }
    }

    public async Task RefreshAsync(bool silent = false)
    {
        if (!CanRefresh())
        {
            return;
        }

        using var operation = _deviceEnvironmentGate.TryEnter();
        if (operation is null)
        {
            return;
        }

        await RefreshCoreAsync(silent);
    }

    private async Task RefreshCoreAsync(bool silent)
    {
        IsRefreshing = true;
        try
        {
            await RefreshPrerequisitesCoreAsync();
            if (!HasAppleDeviceSupport || !AreDeviceToolsAvailable)
            {
                ClearDevices();
                return;
            }

            var previousUdid = SelectedDevice?.Udid;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            var devices = await _deviceService.GetConnectedDevicesAsync(timeout.Token);
            Items.Clear();
            foreach (var device in devices)
            {
                Items.Add(device);
            }

            SelectedDevice = Items.FirstOrDefault(device =>
                                 string.Equals(device.Udid, previousUdid, StringComparison.OrdinalIgnoreCase))
                             ?? Items.FirstOrDefault();
            StatusMessage = devices.Count == 0
                ? "No devices found. Unlock your iPhone, connect it with a data cable, and select \"Trust.\""
                : $"Connected devices: {devices.Count}.";
        }
        catch (Exception exception)
        {
            ClearDevices();
            StatusMessage = exception.Message;
            if (!silent)
            {
                _addActivity("Device refresh failed", exception.Message, false);
            }
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task RefreshPrerequisitesCoreAsync()
    {
        ApplyAppleDeviceSupportStatus(
            await _prerequisiteService.GetAppleDeviceSupportStatusAsync());
    }

    private bool CanRefresh()
    {
        return !IsRefreshing &&
               !IsInstalling &&
               !_deviceEnvironmentGate.IsBusy;
    }

    private bool CanPair()
    {
        return !IsInstalling &&
               !IsRefreshing &&
               !_deviceEnvironmentGate.IsBusy &&
               HasOperationalDeviceEnvironment() &&
               SelectedDevice is not null;
    }

    private bool HasOperationalDeviceEnvironment()
    {
        return HasAppleDeviceSupport && AreDeviceToolsAvailable;
    }

    private bool HasValidInstallTarget()
    {
        return SelectedDevice?.IsPaired == true &&
               File.Exists(SelectedIpaPath) &&
               string.Equals(
                   Path.GetExtension(SelectedIpaPath),
                   ".ipa",
                   StringComparison.OrdinalIgnoreCase);
    }

    private void ClearDevices()
    {
        Items.Clear();
        SelectedDevice = null;
    }

    private async Task PairAsync()
    {
        using var operation = _deviceEnvironmentGate.TryEnter();
        if (operation is null || !HasOperationalDeviceEnvironment())
        {
            return;
        }

        var device = SelectedDevice;
        if (device is null)
        {
            return;
        }

        try
        {
            StatusMessage = "Keep your device unlocked and select \"Trust\" on the device.";
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await _deviceService.PairAsync(device, timeout.Token);
            _addActivity("Device paired", $"{device.Name} now trusts this computer", true);
            await RefreshCoreAsync(silent: false);
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            _addActivity("Device pairing failed", exception.Message, false);
        }
    }

    private bool CanInstall()
    {
        return !IsInstalling &&
               !IsRefreshing &&
               !_deviceEnvironmentGate.IsBusy &&
               HasOperationalDeviceEnvironment() &&
               HasValidInstallTarget();
    }

    private async Task InstallAsync()
    {
        using var operation = _deviceEnvironmentGate.TryEnter();
        if (operation is null ||
            !HasOperationalDeviceEnvironment() ||
            !HasValidInstallTarget())
        {
            return;
        }

        var device = SelectedDevice;
        var ipaPath = SelectedIpaPath;
        if (device is null)
        {
            return;
        }

        var installationCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(20));
        _installationCancellation = installationCancellation;
        IsInstalling = true;
        InstallationProgress = 0;
        InstallationLog = string.Empty;
        StatusMessage = $"Installing on {device.Name}…";
        try
        {
            var progress = new Progress<double>(value => InstallationProgress = value);
            await _deviceService.InstallAsync(
                device,
                ipaPath,
                progress,
                line =>
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        return;
                    }

                    App.Current.Dispatcher.Invoke(() =>
                    {
                        var updated = $"{InstallationLog}{line}{Environment.NewLine}";
                        InstallationLog = updated.Length > 6000 ? updated[^6000..] : updated;
                    });
                },
                installationCancellation.Token);
            StatusMessage = $"Installation complete: {Path.GetFileName(ipaPath)}";
            _addActivity("Device installation complete", $"{Path.GetFileName(ipaPath)} → {device.Name}", true);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Stopped waiting for the installation result. The device-side operation may still be finishing; refresh later to confirm.";
            _addActivity("Stopped waiting for device installation", Path.GetFileName(ipaPath), false);
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            _addActivity("Device installation failed", exception.Message, false);
        }
        finally
        {
            installationCancellation.Dispose();
            if (ReferenceEquals(_installationCancellation, installationCancellation))
            {
                _installationCancellation = null;
            }

            IsInstalling = false;
        }
    }

    private void CancelInstall() => _installationCancellation?.Cancel();

    private void DeviceEnvironmentGateOnStateChanged(object? sender, EventArgs eventArgs)
    {
        RefreshCommand.NotifyCanExecuteChanged();
        PairCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
    }

    private static string FormatVersion(string? version)
    {
        return string.IsNullOrWhiteSpace(version) ? string.Empty : $" {version}";
    }

    private static string FormatBackendName(string? backendName)
    {
        return string.IsNullOrWhiteSpace(backendName) ? "configured backend" : backendName;
    }

    public void Dispose()
    {
        _deviceEnvironmentGate.StateChanged -= DeviceEnvironmentGateOnStateChanged;
        _refreshTimer.Stop();
        _installationCancellation?.Cancel();
        _installationCancellation?.Dispose();
    }
}
