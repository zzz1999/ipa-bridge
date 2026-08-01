using System.Collections.ObjectModel;
using System.ComponentModel;
using IPABridge.Infrastructure;
using IPABridge.Models;
using IPABridge.Services;

namespace IPABridge.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly ConfigurationService _configurationService;
    private readonly ToolBootstrapService _toolBootstrapService;
    private NavigationPage _currentPage = NavigationPage.Dashboard;
    private bool _isInitialized;
    private string _initializationStatus = "Preparing IPA Bridge…";

    public MainViewModel()
    {
        AppPaths.EnsureDirectories();

        _configurationService = new ConfigurationService();
        var processRunner = new ProcessRunner();
        var conPtyRunner = new ConPtyProcessRunner();
        var toolLocationService = new ToolLocationService(_configurationService);
        _toolBootstrapService = new ToolBootstrapService(_configurationService);
        var ipatoolService = new IpatoolService(toolLocationService, processRunner, conPtyRunner);
        var deviceService = new DeviceService(toolLocationService, processRunner);
        var prerequisiteService = new SystemPrerequisiteService(processRunner, toolLocationService);
        var deviceEnvironmentGate = new DeviceEnvironmentOperationGate();

        void AddActivity(string title, string detail, bool success) =>
            App.Current.Dispatcher.Invoke(() =>
            {
                Activities.Insert(0, new ActivityEntry
                {
                    Title = title,
                    Detail = detail,
                    IsSuccess = success
                });
                while (Activities.Count > 12)
                {
                    Activities.RemoveAt(Activities.Count - 1);
                }
            });

        Settings = new SettingsViewModel(
            _configurationService,
            toolLocationService,
            _toolBootstrapService,
            ipatoolService,
            prerequisiteService,
            deviceEnvironmentGate,
            AddActivity);
        Store = new StoreViewModel(_configurationService, ipatoolService, AddActivity);
        Library = new LibraryViewModel(_configurationService, new IpaLibraryService());
        Devices = new DevicesViewModel(
            _configurationService,
            deviceService,
            prerequisiteService,
            deviceEnvironmentGate,
            AddActivity);

        NavigationItems =
        [
            new NavigationItem { Label = "Overview", Page = NavigationPage.Dashboard, IsActive = true },
            new NavigationItem { Label = "App Store", Page = NavigationPage.Store },
            new NavigationItem { Label = "IPA Library", Page = NavigationPage.Library },
            new NavigationItem { Label = "Devices", Page = NavigationPage.Devices },
            new NavigationItem { Label = "Settings", Page = NavigationPage.Settings }
        ];

        NavigateCommand = new RelayCommand(
            parameter =>
            {
                if (parameter is NavigationPage page)
                {
                    CurrentPage = page;
                }
            },
            parameter => parameter is NavigationPage page &&
                         (CurrentPage != NavigationPage.Store ||
                          page == NavigationPage.Store ||
                          !Store.IsBusy));
        QueueIpaCommand = new RelayCommand(parameter =>
        {
            if (parameter is LocalIpa ipa)
            {
                Devices.SelectedIpaPath = ipa.FilePath;
                CurrentPage = NavigationPage.Devices;
            }
        });
        RefreshAllCommand = new AsyncRelayCommand(
            RefreshAllAsync,
            exceptionHandler: exception => Settings.ReportError("Could not refresh application status", exception));

        Store.IpaDownloaded += StoreOnIpaDownloaded;
        Settings.AppleDeviceSupportChanged += status =>
        {
            Devices.ApplyAppleDeviceSupportStatus(status);
            OnPropertyChanged(nameof(OverallReadiness));
        };
        Settings.ToolsChanged += SettingsOnToolsChanged;
        Settings.PropertyChanged += ChildPropertyChanged;
        Devices.PropertyChanged += ChildPropertyChanged;
        Store.PropertyChanged += StorePropertyChanged;
    }

    public IReadOnlyList<NavigationItem> NavigationItems { get; }

    public ObservableCollection<ActivityEntry> Activities { get; } = [];

    public SettingsViewModel Settings { get; }

    public StoreViewModel Store { get; }

    public LibraryViewModel Library { get; }

    public DevicesViewModel Devices { get; }

    public RelayCommand NavigateCommand { get; }

    public RelayCommand QueueIpaCommand { get; }

    public AsyncRelayCommand RefreshAllCommand { get; }

    public NavigationPage CurrentPage
    {
        get => _currentPage;
        set
        {
            if (_currentPage == NavigationPage.Store &&
                value != NavigationPage.Store &&
                Store.IsBusy)
            {
                return;
            }

            if (_currentPage == NavigationPage.Store && value != NavigationPage.Store)
            {
                // Discard the view-model copies of user-entered secrets when leaving the Store page.
                Store.LeaveStore();
            }

            if (!SetProperty(ref _currentPage, value))
            {
                return;
            }

            foreach (var item in NavigationItems)
            {
                item.IsActive = item.Page == value;
            }

            OnPropertyChanged(nameof(PageTitle));
            OnPropertyChanged(nameof(PageSubtitle));
        }
    }

    public string PageTitle => CurrentPage switch
    {
        NavigationPage.Dashboard => "Overview",
        NavigationPage.Store => "App Store",
        NavigationPage.Library => "IPA Library",
        NavigationPage.Devices => "Devices & Installation",
        NavigationPage.Settings => "Settings",
        _ => "IPA Bridge"
    };

    public string PageSubtitle => CurrentPage switch
    {
        NavigationPage.Dashboard => "A clear path from the App Store to your iPhone.",
        NavigationPage.Store => "Search, acquire licenses, and download encrypted App Store IPAs.",
        NavigationPage.Library => "Browse downloaded IPAs and choose files to install.",
        NavigationPage.Devices => "Connect and trust devices, then install valid IPAs.",
        NavigationPage.Settings => "Configure components, download locations, and privacy preferences.",
        _ => string.Empty
    };

    public bool IsInitialized
    {
        get => _isInitialized;
        private set => SetProperty(ref _isInitialized, value);
    }

    public string InitializationStatus
    {
        get => _initializationStatus;
        private set => SetProperty(ref _initializationStatus, value);
    }

    public string OverallReadiness
    {
        get
        {
            if (!Settings.IsIpatoolAvailable)
            {
                return "ipatool installation required";
            }

            if (!Settings.AreDeviceToolsAvailable || !Devices.HasAppleDeviceSupport)
            {
                return "Device setup incomplete";
            }

            return "All components ready";
        }
    }

    public async Task InitializeAsync()
    {
        if (IsInitialized)
        {
            return;
        }

        InitializationStatus = "Loading settings…";
        await _configurationService.LoadAsync();
        Store.LoadConfiguration();

        InitializationStatus = "Checking tools…";
        await Settings.RefreshStatusAsync();

        InitializationStatus = "Restoring Apple Account session…";
        await Store.RestoreSelectedAccountSessionAsync();

        InitializationStatus = "Scanning IPAs…";
        await Library.RefreshAsync();

        InitializationStatus = "Checking the device environment…";
        await Devices.InitializeAsync();

        Activities.Add(new ActivityEntry
        {
            Title = "IPA Bridge started",
            Detail = OverallReadiness,
            IsSuccess = true
        });
        IsInitialized = true;
        InitializationStatus = string.Empty;
        OnPropertyChanged(nameof(OverallReadiness));
    }

    private async Task RefreshAllAsync()
    {
        await Settings.RefreshStatusAsync();
        await Store.RestoreSelectedAccountSessionAsync();
        await Library.RefreshAsync();
        await Devices.RefreshAsync();
        OnPropertyChanged(nameof(OverallReadiness));
    }

    private async void StoreOnIpaDownloaded(object? sender, string path)
    {
        try
        {
            Devices.SelectedIpaPath = path;
            await Library.RefreshAsync();
        }
        catch (Exception exception)
        {
            Library.ReportError("Could not refresh the IPA library after the download", exception);
        }
    }

    private async void SettingsOnToolsChanged(object? sender, EventArgs eventArgs)
    {
        try
        {
            Store.RefreshToolAvailability();
            await Devices.RefreshAsync();
            OnPropertyChanged(nameof(OverallReadiness));
        }
        catch (Exception exception)
        {
            Settings.ReportError("Could not refresh device status after the settings change", exception);
        }
    }

    private void ChildPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (sender == Settings &&
            eventArgs.PropertyName == nameof(SettingsViewModel.AutomaticallyRefreshDevices))
        {
            Devices.ApplyAutomaticRefreshPreference();
        }

        if (eventArgs.PropertyName is nameof(SettingsViewModel.IpatoolStatus)
            or nameof(SettingsViewModel.DeviceToolsStatus)
            or nameof(DevicesViewModel.HasAppleDeviceSupport))
        {
            OnPropertyChanged(nameof(OverallReadiness));
        }
    }

    private void StorePropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(StoreViewModel.IsBusy))
        {
            NavigateCommand.NotifyCanExecuteChanged();
        }
    }

    public void Dispose()
    {
        Store.IpaDownloaded -= StoreOnIpaDownloaded;
        Settings.ToolsChanged -= SettingsOnToolsChanged;
        Store.PropertyChanged -= StorePropertyChanged;
        Store.LeaveStore();
        Settings.Dispose();
        Devices.Dispose();
        _toolBootstrapService.Dispose();
    }
}
