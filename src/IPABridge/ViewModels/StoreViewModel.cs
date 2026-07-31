using System.Collections.ObjectModel;
using IPABridge.Infrastructure;
using IPABridge.Models;
using IPABridge.Services;

namespace IPABridge.ViewModels;

public sealed class StoreViewModel : ObservableObject
{
    private readonly ConfigurationService _configurationService;
    private readonly IpatoolService _ipatoolService;
    private readonly Action<string, string, bool> _addActivity;
    private string _email = string.Empty;
    private string _applePassword = string.Empty;
    private string _twoFactorCode = string.Empty;
    private string _vaultPassphrase = string.Empty;
    private string _searchQuery = string.Empty;
    private StoreApp? _selectedApp;
    private StoreAppVersion? _selectedVersion;
    private CancellationTokenSource? _versionLookupCancellation;
    private bool _isBusy;
    private bool _isLoggedIn;
    private bool _requiresTwoFactor;
    private bool _isVerboseLoggingTipVisible = true;
    private string _statusMessage = "Install ipatool to connect to the App Store.";
    private string _operationTitle = string.Empty;

    public StoreViewModel(
        ConfigurationService configurationService,
        IpatoolService ipatoolService,
        Action<string, string, bool> addActivity)
    {
        _configurationService = configurationService;
        _ipatoolService = ipatoolService;
        _addActivity = addActivity;

        LoginCommand = new AsyncRelayCommand(LoginAsync, CanLogin);
        CheckAccountCommand = new AsyncRelayCommand(CheckAccountAsync, CanCheckAccount);
        SearchCommand = new AsyncRelayCommand(SearchAsync, CanSearch);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, CanDownload);
        LoadVersionsCommand = new AsyncRelayCommand(LoadVersionsAsync, CanDownload);
        DismissVerboseLoggingTipCommand = new RelayCommand(() => IsVerboseLoggingTipVisible = false);
    }

    public event EventHandler<string>? IpaDownloaded;

    public ObservableCollection<StoreApp> SearchResults { get; } = [];

    public ObservableCollection<StoreAppVersion> Versions { get; } = [];

    public AsyncRelayCommand LoginCommand { get; }

    public AsyncRelayCommand CheckAccountCommand { get; }

    public AsyncRelayCommand SearchCommand { get; }

    public AsyncRelayCommand DownloadCommand { get; }

    public AsyncRelayCommand LoadVersionsCommand { get; }

    public RelayCommand DismissVerboseLoggingTipCommand { get; }

    public string Email
    {
        get => _email;
        set
        {
            if (SetProperty(ref _email, value))
            {
                NotifyCommands();
            }
        }
    }

    public string ApplePassword
    {
        get => _applePassword;
        set
        {
            if (SetProperty(ref _applePassword, value))
            {
                NotifyCommands();
            }
        }
    }

    public string TwoFactorCode
    {
        get => _twoFactorCode;
        set => SetProperty(ref _twoFactorCode, value);
    }

    public string VaultPassphrase
    {
        get => _vaultPassphrase;
        set
        {
            if (SetProperty(ref _vaultPassphrase, value))
            {
                NotifyCommands();
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                SearchCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public StoreApp? SelectedApp
    {
        get => _selectedApp;
        set
        {
            if (!SetProperty(ref _selectedApp, value))
            {
                return;
            }

            _versionLookupCancellation?.Cancel();
            Versions.Clear();
            SelectedVersion = null;
            DownloadCommand.NotifyCanExecuteChanged();
            LoadVersionsCommand.NotifyCanExecuteChanged();
        }
    }

    public StoreAppVersion? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
            {
                OnPropertyChanged(nameof(SelectedVersionIdentifier));
            }
        }
    }

    public string? SelectedVersionIdentifier => SelectedVersion?.ExternalVersionIdentifier;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            NotifyCommands();
        }
    }

    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        private set => SetProperty(ref _isLoggedIn, value);
    }

    public bool RequiresTwoFactor
    {
        get => _requiresTwoFactor;
        private set => SetProperty(ref _requiresTwoFactor, value);
    }

    public bool IsVerboseLoggingTipVisible
    {
        get => _isVerboseLoggingTipVisible;
        private set => SetProperty(ref _isVerboseLoggingTipVisible, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string OperationTitle
    {
        get => _operationTitle;
        private set => SetProperty(ref _operationTitle, value);
    }

    public void LoadConfiguration()
    {
        Email = _configurationService.Current.AppleAccountEmail;
        StatusMessage = _ipatoolService.IsAvailable
            ? "Enter your local vault passphrase to check an existing sign-in or reconnect your account."
            : "Install the official ipatool from Settings first.";
    }

    public void ClearSecrets()
    {
        ApplePassword = string.Empty;
        TwoFactorCode = string.Empty;
        VaultPassphrase = string.Empty;
        RequiresTwoFactor = false;
    }

    private bool CanLogin()
    {
        return !IsBusy &&
               _ipatoolService.IsAvailable &&
               !string.IsNullOrWhiteSpace(Email) &&
               !string.IsNullOrWhiteSpace(ApplePassword) &&
               !string.IsNullOrWhiteSpace(VaultPassphrase);
    }

    private bool CanCheckAccount()
    {
        return !IsBusy &&
               _ipatoolService.IsAvailable &&
               !string.IsNullOrWhiteSpace(VaultPassphrase);
    }

    private bool CanSearch()
    {
        return !IsBusy &&
               _ipatoolService.IsAvailable &&
               !string.IsNullOrWhiteSpace(SearchQuery) &&
               !string.IsNullOrWhiteSpace(VaultPassphrase);
    }

    private bool CanDownload()
    {
        return !IsBusy &&
               SelectedApp is not null &&
               !string.IsNullOrWhiteSpace(VaultPassphrase);
    }

    private async Task LoginAsync()
    {
        await RunBusyAsync("Signing in securely…", async () =>
        {
            var result = await _ipatoolService.LoginAsync(
                Email.Trim(),
                ApplePassword,
                string.IsNullOrWhiteSpace(TwoFactorCode) ? null : TwoFactorCode.Trim(),
                VaultPassphrase);
            IsLoggedIn = result.Success;
            RequiresTwoFactor = result.RequiresTwoFactor;
            StatusMessage = result.Message;
            if (result.Success)
            {
                _configurationService.Current.AppleAccountEmail = Email.Trim();
                await _configurationService.SaveAsync();
            }

            _addActivity("App Store sign-in", result.Message, result.Success);
        });
        ApplePassword = string.Empty;
        TwoFactorCode = string.Empty;
    }

    private async Task CheckAccountAsync()
    {
        await RunBusyAsync("Checking account…", async () =>
        {
            IsLoggedIn = await _ipatoolService.HasStoredAccountAsync(VaultPassphrase);
            StatusMessage = IsLoggedIn
                ? "A valid App Store sign-in was found."
                : "No valid sign-in was found. Reconnect your account.";
        });
    }

    private async Task SearchAsync()
    {
        await RunBusyAsync("Searching the App Store…", async () =>
        {
            var apps = await _ipatoolService.SearchAsync(SearchQuery.Trim(), VaultPassphrase);
            SearchResults.Clear();
            foreach (var app in apps)
            {
                SearchResults.Add(app);
            }

            SelectedApp = SearchResults.FirstOrDefault();
            StatusMessage = apps.Count == 0
                ? "No matching iPhone apps were found."
                : $"Results found: {apps.Count}.";
            _addActivity("App Store search", $"{SearchQuery.Trim()} — Results: {apps.Count}", true);
        });
    }

    private async Task LoadVersionsAsync()
    {
        var app = SelectedApp;
        if (app is null)
        {
            return;
        }

        _versionLookupCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        _versionLookupCancellation = cancellation;
        var resolvedByIndex = new SortedDictionary<int, StoreAppVersion>();
        var progressCompleted = false;
        var progress = new Progress<StoreVersionLookupProgress>(update =>
        {
            if (progressCompleted ||
                !ReferenceEquals(_versionLookupCancellation, cancellation) ||
                !ReferenceEquals(SelectedApp, app))
            {
                return;
            }

            resolvedByIndex[update.Index] = update.Version;
            Versions.Clear();
            foreach (var version in resolvedByIndex.Values)
            {
                Versions.Add(version);
            }

            OperationTitle = $"Resolving version details {update.Completed}/{update.Total}…";
        });

        try
        {
            await RunBusyAsync("Loading version identifiers…", async () =>
            {
                IReadOnlyList<StoreAppVersion> versions;
                try
                {
                    versions = await _ipatoolService.ListVersionsAsync(
                        app,
                        VaultPassphrase,
                        progress,
                        cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    return;
                }

                progressCompleted = true;
                if (!ReferenceEquals(_versionLookupCancellation, cancellation) ||
                    !ReferenceEquals(SelectedApp, app))
                {
                    return;
                }

                Versions.Clear();
                foreach (var version in versions)
                {
                    Versions.Add(version);
                }

                SelectedVersion = null;
                var resolvedCount = versions.Count(version => version.HasMetadata);
                StatusMessage = BuildVersionStatusMessage(versions, resolvedCount);
            });
        }
        finally
        {
            progressCompleted = true;
            if (ReferenceEquals(_versionLookupCancellation, cancellation))
            {
                _versionLookupCancellation = null;
            }
        }
    }

    private async Task DownloadAsync()
    {
        if (SelectedApp is null)
        {
            return;
        }

        var app = SelectedApp;
        await RunBusyAsync($"Downloading {app.Name}…", async () =>
        {
            var path = await _ipatoolService.DownloadAsync(
                app,
                _configurationService.Current.DownloadDirectory,
                VaultPassphrase,
                SelectedVersionIdentifier);
            StatusMessage = $"Download complete: {Path.GetFileName(path)}";
            _addActivity("IPA download complete", $"{app.Name} — {Path.GetFileName(path)}", true);
            IpaDownloaded?.Invoke(this, path);
        });
    }

    private async Task RunBusyAsync(string title, Func<Task> action)
    {
        IsBusy = true;
        OperationTitle = title;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            StatusMessage = exception.Message;
            _addActivity("App Store operation failed", exception.Message, false);
        }
        finally
        {
            IsBusy = false;
            OperationTitle = string.Empty;
        }
    }

    private void NotifyCommands()
    {
        LoginCommand.NotifyCanExecuteChanged();
        CheckAccountCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        LoadVersionsCommand.NotifyCanExecuteChanged();
    }

    private static string BuildVersionStatusMessage(
        IReadOnlyList<StoreAppVersion> versions,
        int resolvedCount)
    {
        if (versions.Count == 0)
        {
            return "No version history was returned.";
        }

        if (resolvedCount == versions.Count)
        {
            return $"Loaded {versions.Count} historical versions with version numbers and release dates. Leave the selection blank to download the latest version.";
        }

        if (resolvedCount > 0)
        {
            return $"Loaded {versions.Count} historical versions and resolved {resolvedCount}. The remaining entries retain their original version identifiers.";
        }

        if (versions.Any(version => version.RequiresLicense))
        {
            return "Version identifiers were loaded, but the App Store requires a license for this app before version numbers can be resolved.";
        }

        return "Version identifiers were loaded, but the current ipatool could not resolve the version numbers. Update ipatool and try again.";
    }
}
