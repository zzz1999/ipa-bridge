using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Security.Cryptography;
using IPABridge.Infrastructure;
using IPABridge.Models;
using IPABridge.Services;

namespace IPABridge.ViewModels;

public sealed class StoreViewModel : ObservableObject
{
    private readonly ConfigurationService _configurationService;
    private readonly IpatoolService _ipatoolService;
    private readonly Action<string, string, bool> _addActivity;
    private AppleAccountProfile? _selectedAccount;
    private IpatoolAccountInfo? _activeAccount;
    private string? _pendingAccountId;
    private string _email = string.Empty;
    private string _applePassword = string.Empty;
    private string _twoFactorCode = string.Empty;
    private string? _transientLocalVaultAccountId;
    private string _transientLocalVaultKey = string.Empty;
    private string _searchQuery = string.Empty;
    private StoreApp? _selectedApp;
    private StoreAppVersion? _selectedVersion;
    private CancellationTokenSource? _versionLookupCancellation;
    private CancellationTokenSource? _operationCancellation;
    private bool _leaveStoreCleanupPending;
    private bool _isBusy;
    private bool _isLoggedIn;
    private bool _isAddingAccount;
    private bool _isRemoveConfirmationVisible;
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
        AddAccountCommand = new RelayCommand(BeginAddAccount, () => !IsBusy && !IsAddingAccount);
        CancelAccountEditCommand = new RelayCommand(CancelAccountEdit, () => IsAddingAccount && !IsBusy);
        RequestRemoveAccountCommand = new RelayCommand(
            () => IsRemoveConfirmationVisible = true,
            () => SelectedAccount is not null && !IsBusy && !IsAddingAccount);
        CancelRemoveAccountCommand = new RelayCommand(
            () => IsRemoveConfirmationVisible = false,
            () => IsRemoveConfirmationVisible && !IsBusy);
        ConfirmRemoveAccountCommand = new AsyncRelayCommand(
            RemoveSelectedAccountAsync,
            () => SelectedAccount is not null && !IsBusy && IsRemoveConfirmationVisible);
        CancelTwoFactorCommand = new RelayCommand(
            CancelTwoFactor,
            () => RequiresTwoFactor && !IsBusy);
        DismissVerboseLoggingTipCommand = new RelayCommand(() => IsVerboseLoggingTipVisible = false);

        // Keep every account-list mutation, including rollback paths, reflected in the selector state.
        Accounts.CollectionChanged += (_, _) => NotifyAccountCollectionStateChanged();
    }

    public event EventHandler<string>? IpaDownloaded;

    public ObservableCollection<StoreApp> SearchResults { get; } = [];

    public ObservableCollection<StoreAppVersion> Versions { get; } = [];

    public ObservableCollection<AppleAccountProfile> Accounts { get; } = [];

    public AsyncRelayCommand LoginCommand { get; }

    public AsyncRelayCommand CheckAccountCommand { get; }

    public AsyncRelayCommand SearchCommand { get; }

    public AsyncRelayCommand DownloadCommand { get; }

    public AsyncRelayCommand LoadVersionsCommand { get; }

    public RelayCommand AddAccountCommand { get; }

    public RelayCommand CancelAccountEditCommand { get; }

    public RelayCommand RequestRemoveAccountCommand { get; }

    public RelayCommand CancelRemoveAccountCommand { get; }

    public AsyncRelayCommand ConfirmRemoveAccountCommand { get; }

    public RelayCommand CancelTwoFactorCommand { get; }

    public RelayCommand DismissVerboseLoggingTipCommand { get; }

    public AppleAccountProfile? SelectedAccount
    {
        get => _selectedAccount;
        private set
        {
            var previousAccount = _selectedAccount;
            if (!SetProperty(ref _selectedAccount, value))
            {
                return;
            }

            if (previousAccount is not null)
            {
                previousAccount.PropertyChanged -= SelectedAccountOnPropertyChanged;
            }

            if (value is not null)
            {
                value.PropertyChanged += SelectedAccountOnPropertyChanged;
            }

            OnPropertyChanged(nameof(SelectedAccountSummary));
            OnPropertyChanged(nameof(StorefrontSummary));
            OnPropertyChanged(nameof(AccountFormTitle));
            OnPropertyChanged(nameof(AccountActionLabel));
            OnPropertyChanged(nameof(RequiresLegacySessionReset));
            OnPropertyChanged(nameof(CanSelectAccount));
            OnPropertyChanged(nameof(IsAccountFormVisible));
            NotifyCommands();
        }
    }

    private void SelectedAccountOnPropertyChanged(
        object? sender,
        PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(AppleAccountProfile.LocalVaultKey))
        {
            OnPropertyChanged(nameof(AccountActionLabel));
            OnPropertyChanged(nameof(RequiresLegacySessionReset));
            NotifyCommands();
            return;
        }

        if (eventArgs.PropertyName != nameof(AppleAccountProfile.Email))
        {
            return;
        }

        if (!IsAddingAccount)
        {
            Email = SelectedAccount?.Email ?? string.Empty;
        }

        OnPropertyChanged(nameof(SelectedAccountSummary));
        OnPropertyChanged(nameof(StorefrontSummary));
    }

    public string SelectedAccountSummary => SelectedAccount is null
        ? "No Apple Account selected"
        : $"Selected account: {SelectedAccount.Email}";

    public string StorefrontSummary => SelectedAccount is not null
        ? $"Searches and purchases use the App Store region assigned to {SelectedAccount.Email}."
        : IsAddingAccount
            ? "The App Store region will be detected after secure sign-in."
            : HasAccounts
                ? "Select an account to choose an App Store region."
                : "Add an Apple Account to choose an App Store region.";

    public string SearchActionHelp
    {
        get
        {
            if (!_ipatoolService.IsAvailable)
            {
                return "Install ipatool before searching.";
            }

            if (RequiresTwoFactor)
            {
                return "Finish Apple verification before searching.";
            }

            if (IsAddingAccount)
            {
                return "Finish adding or cancel this Apple Account before searching.";
            }

            if (SelectedAccount is null)
            {
                return "Select an Apple Account before searching.";
            }

            if (!HasLocalVaultKey(SelectedAccount))
            {
                return "Reset this profile's local session and sign in before searching.";
            }

            if (!IsLoggedIn)
            {
                return "Check the selected account session or sign in before searching.";
            }

            return string.IsNullOrWhiteSpace(SearchQuery)
                ? "Enter an app name to search."
                : $"Search {SelectedAccount.Email}'s App Store region.";
        }
    }

    public string AccountFormTitle => IsAddingAccount
        ? "Add Apple Account"
        : SelectedAccount is null
            ? "Add Apple Account"
            : "Reconnect Selected Account";

    public string AccountActionLabel => IsAddingAccount
        ? "Add & Sign In"
        : RequiresLegacySessionReset
            ? "Reset Session & Sign In"
            : "Sign In Securely";

    public bool RequiresLegacySessionReset =>
        !IsAddingAccount && SelectedAccount is not null && !HasLocalVaultKey(SelectedAccount);

    public bool HasAccounts => Accounts.Count > 0;

    public bool CanSelectAccount =>
        HasAccounts && SelectedAccount is not null && !IsBusy && !IsAddingAccount;

    public bool IsEmptyAccountPromptVisible => !HasAccounts && !IsAddingAccount;

    public bool IsAccountSelectionSectionVisible => HasAccounts || !IsAddingAccount;

    public bool IsAccountFormVisible => IsAddingAccount || SelectedAccount is not null;

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
        set
        {
            var normalized = new string((value ?? string.Empty)
                .Where(character => character is >= '0' and <= '9')
                .Take(6)
                .ToArray());
            if (SetProperty(ref _twoFactorCode, normalized))
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
                OnPropertyChanged(nameof(SearchActionHelp));
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

            OnPropertyChanged(nameof(CanSelectAccount));
            NotifyCommands();
        }
    }

    public bool IsLoggedIn
    {
        get => _isLoggedIn;
        private set => SetProperty(ref _isLoggedIn, value);
    }

    public bool IsAddingAccount
    {
        get => _isAddingAccount;
        private set
        {
            if (!SetProperty(ref _isAddingAccount, value))
            {
                return;
            }

            OnPropertyChanged(nameof(AccountFormTitle));
            OnPropertyChanged(nameof(AccountActionLabel));
            OnPropertyChanged(nameof(RequiresLegacySessionReset));
            OnPropertyChanged(nameof(IsAccountEmailReadOnly));
            OnPropertyChanged(nameof(CanSelectAccount));
            OnPropertyChanged(nameof(StorefrontSummary));
            OnPropertyChanged(nameof(IsEmptyAccountPromptVisible));
            OnPropertyChanged(nameof(IsAccountSelectionSectionVisible));
            OnPropertyChanged(nameof(IsAccountFormVisible));
            NotifyCommands();
        }
    }

    public bool IsAccountEmailReadOnly => !IsAddingAccount;

    public bool IsRemoveConfirmationVisible
    {
        get => _isRemoveConfirmationVisible;
        private set
        {
            if (SetProperty(ref _isRemoveConfirmationVisible, value))
            {
                NotifyCommands();
            }
        }
    }

    public string ActiveAccountLabel => _activeAccount is null
        ? "Account session not checked"
        : string.IsNullOrWhiteSpace(_activeAccount.Name)
            ? $"Connected as {_activeAccount.Email}"
            : $"Connected as {_activeAccount.Name} ({_activeAccount.Email})";

    public bool RequiresTwoFactor
    {
        get => _requiresTwoFactor;
        private set
        {
            if (SetProperty(ref _requiresTwoFactor, value))
            {
                CancelTwoFactorCommand.NotifyCanExecuteChanged();
                NotifyCommands();
            }
        }
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
        foreach (var recoveryFailure in _ipatoolService.RecoverStagedLocalAccountSessionRemovals(
                     _configurationService.Current.AppleAccounts.Select(account => account.Id)))
        {
            _addActivity("Local account recovery failed", recoveryFailure, false);
        }

        Accounts.Clear();
        foreach (var account in _configurationService.Current.AppleAccounts)
        {
            Accounts.Add(account);
        }

        var selectedAccount = Accounts.FirstOrDefault(account => string.Equals(
                                  account.Id,
                                  _configurationService.Current.SelectedAppleAccountId,
                                  StringComparison.Ordinal))
                              ?? Accounts.FirstOrDefault();
        ApplySelectedAccount(selectedAccount, clearSecrets: false);
        StatusMessage = _ipatoolService.IsAvailable
            ? selectedAccount is null
                ? "Add an Apple Account to search its App Store region."
                : BuildAccountReadyMessage(selectedAccount)
            : "Install the official ipatool from Settings first.";
    }

    public void RefreshToolAvailability()
    {
        if (!_ipatoolService.IsAvailable)
        {
            ClearAccountOperationState(clearSecrets: true);
            StatusMessage = TryRemoveTransientLocalAccountSession()
                ? "Install the official ipatool from Settings first."
                : "ipatool is unavailable, and a temporary local session could not be removed. Retry before changing accounts.";
        }
        else if (IsAddingAccount)
        {
            StatusMessage = "ipatool is ready. Enter your Apple Account details to continue.";
        }
        else if (SelectedAccount is null)
        {
            StatusMessage = "Add an Apple Account to search its App Store region.";
        }
        else
        {
            StatusMessage = BuildAccountReadyMessage(SelectedAccount);
        }

        NotifyCommands();
    }

    public async Task SelectAccountAsync(AppleAccountProfile? account)
    {
        if (IsBusy || account is null || !Accounts.Contains(account))
        {
            return;
        }

        if (ReferenceEquals(SelectedAccount, account) && !IsAddingAccount)
        {
            return;
        }

        var previousAccount = SelectedAccount;
        await RunBusyAsync("Switching Apple Account…", async cancellationToken =>
        {
            if (!RemoveUncommittedAccountSessions())
            {
                OnPropertyChanged(nameof(SelectedAccount));
                throw new IOException(
                    "The temporary account session could not be removed. Retry cleanup before switching accounts.");
            }

            IsAddingAccount = false;
            _pendingAccountId = null;
            IsRemoveConfirmationVisible = false;
            ApplySelectedAccount(account, clearSecrets: true);
            SyncAccountsToConfiguration();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _configurationService.SaveAsync();
            }
            catch
            {
                ApplySelectedAccount(previousAccount, clearSecrets: true);
                SyncAccountsToConfiguration();
                throw;
            }

            StatusMessage = BuildAccountReadyMessage(account);
        });
    }

    public void ClearSecrets()
    {
        ApplePassword = string.Empty;
        TwoFactorCode = string.Empty;
        RequiresTwoFactor = false;
        ClearTransientLocalVaultKey();
    }

    public void LeaveStore()
    {
        _leaveStoreCleanupPending = true;
        _versionLookupCancellation?.Cancel();
        _operationCancellation?.Cancel();
        ClearSecrets();
        if (!IsBusy)
        {
            CompleteLeaveStoreCleanup();
        }
    }

    private void BeginAddAccount()
    {
        if (!RemoveUncommittedAccountSessions())
        {
            StatusMessage = "The previous temporary account session could not be removed. Try adding the account again to retry cleanup.";
            return;
        }

        _pendingAccountId = Guid.NewGuid().ToString("N");
        IsAddingAccount = true;
        IsRemoveConfirmationVisible = false;
        ClearAccountOperationState(clearSecrets: true);
        Email = string.Empty;
        StatusMessage = _ipatoolService.IsAvailable
            ? "Enter another Apple Account. IPA Bridge creates protected local session access automatically."
            : "Install the official ipatool to continue adding this Apple Account.";
    }

    private void CancelAccountEdit()
    {
        if (!RemoveUncommittedAccountSessions())
        {
            StatusMessage = "The temporary account session could not be removed. Select Cancel Adding Account again to retry.";
            return;
        }

        _pendingAccountId = null;
        _leaveStoreCleanupPending = false;
        IsAddingAccount = false;
        Email = SelectedAccount?.Email ?? string.Empty;
        ClearSecrets();
        StatusMessage = SelectedAccount is null
            ? "Add an Apple Account to continue."
            : BuildAccountReadyMessage(SelectedAccount);
    }

    private async Task RemoveSelectedAccountAsync()
    {
        var account = SelectedAccount;
        if (account is null)
        {
            return;
        }

        var accountIndex = Accounts.IndexOf(account);
        await RunBusyAsync("Removing local account profile…", async cancellationToken =>
        {
            if (!RemoveUncommittedAccountSessions())
            {
                throw new IOException(
                    "The temporary account session could not be removed. Retry cleanup before removing this profile.");
            }

            _pendingAccountId = null;
            var stagedSessionRemoval =
                _ipatoolService.StageLocalAccountSessionRemoval(account);
            try
            {
                Accounts.Remove(account);
                IsRemoveConfirmationVisible = false;
                ApplySelectedAccount(Accounts.FirstOrDefault(), clearSecrets: true);
                SyncAccountsToConfiguration();
                cancellationToken.ThrowIfCancellationRequested();
                await _configurationService.SaveAsync();
            }
            catch (Exception saveException)
            {
                if (!Accounts.Contains(account))
                {
                    Accounts.Insert(Math.Clamp(accountIndex, 0, Accounts.Count), account);
                }

                ApplySelectedAccount(account, clearSecrets: true);
                IsRemoveConfirmationVisible = true;
                SyncAccountsToConfiguration();
                try
                {
                    _ipatoolService.RollbackLocalAccountSessionRemoval(
                        stagedSessionRemoval);
                }
                catch (Exception rollbackException) when (
                    rollbackException is IOException or UnauthorizedAccessException)
                {
                    throw new IOException(
                        "The profile save failed and its staged local session could not be restored. " +
                        "IPA Bridge will retry recovery on the next launch.",
                        new AggregateException(saveException, rollbackException));
                }

                throw;
            }

            ForgetTransientLocalVaultState(account.Id);
            try
            {
                _ipatoolService.CommitLocalAccountSessionRemoval(
                    stagedSessionRemoval);
                StatusMessage = $"Removed {account.Email} and its local ipatool session from this PC.";
                _addActivity("Apple Account removed", account.Email, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                StatusMessage =
                    $"Removed {account.Email}. Its isolated local session is quarantined and will be deleted automatically on the next launch: {exception.Message}";
                _addActivity("Local account cleanup pending", exception.Message, false);
            }
        });
    }

    private void ApplySelectedAccount(AppleAccountProfile? account, bool clearSecrets)
    {
        SelectedAccount = account;
        Email = account?.Email ?? string.Empty;
        ClearAccountOperationState(clearSecrets);
    }

    private void ClearAccountOperationState(bool clearSecrets)
    {
        _versionLookupCancellation?.Cancel();
        SearchResults.Clear();
        SelectedApp = null;
        Versions.Clear();
        SelectedVersion = null;
        SetActiveAccount(null);
        RequiresTwoFactor = false;
        if (clearSecrets)
        {
            ClearSecrets();
        }
    }

    private void SetActiveAccount(IpatoolAccountInfo? account)
    {
        _activeAccount = account;
        IsLoggedIn = account is not null &&
                     SelectedAccount is not null &&
                     string.Equals(
                         account.Email,
                         SelectedAccount.Email,
                         StringComparison.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(ActiveAccountLabel));
        NotifyCommands();
    }

    private void SyncAccountsToConfiguration()
    {
        _configurationService.Current.AppleAccounts = Accounts
            .Select(account => new AppleAccountProfile
            {
                Id = account.Id,
                Email = account.Email,
                LocalVaultKey = account.LocalVaultKey
            })
            .ToList();
        _configurationService.Current.SelectedAppleAccountId = SelectedAccount?.Id ?? string.Empty;
    }

    private bool RemovePendingAccountSession()
    {
        if (_pendingAccountId is null)
        {
            return true;
        }

        try
        {
            _ipatoolService.RemoveLocalAccountSession(new AppleAccountProfile
            {
                Id = _pendingAccountId,
                Email = Email
            });
            return true;
        }
        catch (IOException exception)
        {
            _addActivity("Local account cleanup failed", exception.Message, false);
            return false;
        }
        catch (UnauthorizedAccessException exception)
        {
            _addActivity("Local account cleanup failed", exception.Message, false);
            return false;
        }
    }

    private void CompleteLeaveStoreCleanup()
    {
        if (!_leaveStoreCleanupPending)
        {
            return;
        }

        if (RemoveUncommittedAccountSessions())
        {
            _pendingAccountId = null;
            IsAddingAccount = false;
            Email = SelectedAccount?.Email ?? string.Empty;
            _leaveStoreCleanupPending = false;
        }
        else
        {
            StatusMessage = "The temporary account session could not be removed. Return to the App Store page and retry account cleanup.";
        }
    }

    private void CancelTwoFactor()
    {
        var cleanupSucceeded = TryRemoveTransientLocalAccountSession();
        ApplePassword = string.Empty;
        TwoFactorCode = string.Empty;
        RequiresTwoFactor = false;
        SetActiveAccount(null);
        StatusMessage = cleanupSucceeded
            ? "Verification canceled. Enter your Apple Account password to start again."
            : "Verification canceled, but the temporary local session could not be removed. Retry or cancel account setup to clean it up.";
    }

    private string PrepareLocalVaultKey(AppleAccountProfile account)
    {
        if (_transientLocalVaultAccountId is not null &&
            !string.Equals(
                _transientLocalVaultAccountId,
                account.Id,
                StringComparison.Ordinal) &&
            !TryRemoveTransientLocalAccountSession())
        {
            throw new IOException(
                "The previous temporary local account session could not be removed. Retry cleanup before signing in with another profile.");
        }

        if (HasLocalVaultKey(account))
        {
            return account.LocalVaultKey;
        }

        if (string.Equals(
                _transientLocalVaultAccountId,
                account.Id,
                StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(_transientLocalVaultKey))
        {
            return _transientLocalVaultKey;
        }

        // A profile with no saved key cannot safely reuse local ipatool data.
        // Persisted legacy profiles expose this reset explicitly in the action
        // label; new profiles can only have temporary residue from an earlier retry.
        _ipatoolService.RemoveLocalAccountSession(account);

        _transientLocalVaultAccountId = account.Id;
        _transientLocalVaultKey = Convert.ToBase64String(
            RandomNumberGenerator.GetBytes(LocalDataProtectionService.MasterKeySize));
        return _transientLocalVaultKey;
    }

    private void ClearTransientLocalVaultKey()
    {
        _transientLocalVaultKey = string.Empty;
    }

    private void ForgetTransientLocalVaultState()
    {
        _transientLocalVaultAccountId = null;
        _transientLocalVaultKey = string.Empty;
    }

    private void ForgetTransientLocalVaultState(string accountId)
    {
        if (string.Equals(
                _transientLocalVaultAccountId,
                accountId,
                StringComparison.Ordinal))
        {
            ForgetTransientLocalVaultState();
        }
    }

    private bool TryRemoveTransientLocalAccountSession()
    {
        if (_transientLocalVaultAccountId is null)
        {
            ClearTransientLocalVaultKey();
            return true;
        }

        var accountId = _transientLocalVaultAccountId;
        var removed = TryRemoveLocalAccountSession(new AppleAccountProfile
        {
            Id = accountId,
            Email = Email
        });
        ClearTransientLocalVaultKey();
        if (removed)
        {
            ForgetTransientLocalVaultState(accountId);
        }

        return removed;
    }

    private bool RemoveUncommittedAccountSessions()
    {
        if (!RemovePendingAccountSession())
        {
            return false;
        }

        return TryRemoveTransientLocalAccountSession();
    }

    private bool TryRemoveLocalAccountSession(AppleAccountProfile account)
    {
        try
        {
            _ipatoolService.RemoveLocalAccountSession(account);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _addActivity("Local account cleanup failed", exception.Message, false);
            return false;
        }
    }

    private static bool HasLocalVaultKey(AppleAccountProfile account)
    {
        return !string.IsNullOrWhiteSpace(account.LocalVaultKey);
    }

    private static string RequireLocalVaultKey(AppleAccountProfile account)
    {
        return HasLocalVaultKey(account)
            ? account.LocalVaultKey
            : throw new InvalidOperationException(
                $"Reconnect {account.Email} once to create automatic protected local session access.");
    }

    private static bool IsSixDigitVerificationCode(string value)
    {
        return value.Length == 6 && value.All(character => character is >= '0' and <= '9');
    }

    private static string BuildLoginStatusMessage(
        IpatoolLoginResult result,
        bool wasVerifyingTwoFactor)
    {
        if (!wasVerifyingTwoFactor || result.Success)
        {
            return result.Message;
        }

        var normalizedMessage = result.Message.Trim().TrimEnd('.');
        return normalizedMessage.Length == 0 ||
               string.Equals(
                   normalizedMessage,
                   "something went wrong",
                   StringComparison.OrdinalIgnoreCase)
            ? "Apple verification was not accepted. Check the six-digit code and try again."
            : result.Message;
    }

    private static string BuildAccountReadyMessage(AppleAccountProfile account)
    {
        return HasLocalVaultKey(account)
            ? $"Selected {account.Email}. Its local session key is protected automatically; select Check Session to verify the sign-in."
            : $"{account.Email} was saved by an earlier IPA Bridge version. Enter the Apple Account password, then select Reset Session & Sign In to replace only its old local sign-in with automatic protected access.";
    }

    private bool CanLogin()
    {
        return !IsBusy &&
               _ipatoolService.IsAvailable &&
               (IsAddingAccount || SelectedAccount is not null) &&
               !string.IsNullOrWhiteSpace(Email) &&
               !string.IsNullOrWhiteSpace(ApplePassword) &&
               (!RequiresTwoFactor || IsSixDigitVerificationCode(TwoFactorCode));
    }

    private bool CanCheckAccount()
    {
        return !IsBusy &&
               _ipatoolService.IsAvailable &&
               SelectedAccount is not null &&
               !IsAddingAccount &&
               HasLocalVaultKey(SelectedAccount);
    }

    private bool CanSearch()
    {
        return !IsBusy &&
               _ipatoolService.IsAvailable &&
               SelectedAccount is not null &&
               IsLoggedIn &&
               !IsAddingAccount &&
               !string.IsNullOrWhiteSpace(SearchQuery) &&
               HasLocalVaultKey(SelectedAccount);
    }

    private bool CanDownload()
    {
        return !IsBusy &&
               SelectedAccount is not null &&
               IsLoggedIn &&
               !IsAddingAccount &&
               SelectedApp is not null &&
               HasLocalVaultKey(SelectedAccount);
    }

    private async Task LoginAsync()
    {
        var requestedEmail = Email.Trim();
        var matchingAccount = IsAddingAccount
            ? Accounts.FirstOrDefault(existing => string.Equals(
                existing.Email,
                requestedEmail,
                StringComparison.OrdinalIgnoreCase))
            : null;
        if (matchingAccount is not null)
        {
            StatusMessage =
                $"{matchingAccount.Email} already has a local profile. Cancel adding, select that profile, and sign in there.";
            return;
        }

        var account = IsAddingAccount
            ? new AppleAccountProfile
            {
                Id = _pendingAccountId ?? Guid.NewGuid().ToString("N"),
                Email = requestedEmail
            }
            : SelectedAccount;
        if (account is null)
        {
            return;
        }

        var accountWasPersisted = Accounts.Contains(account);
        var originalEmail = account.Email;
        var originalLocalVaultKey = account.LocalVaultKey;
        var previousSelectedAccount = SelectedAccount;

        // A login attempt can replace the isolated ipatool session. Require a
        // fresh confirmed identity before showing the connected state again.
        SetActiveAccount(null);
        await RunBusyAsync("Signing in securely…", async cancellationToken =>
        {
            var wasVerifyingTwoFactor = RequiresTwoFactor;
            var localVaultKey = PrepareLocalVaultKey(account);
            var result = await _ipatoolService.LoginAsync(
                account,
                ApplePassword,
                string.IsNullOrWhiteSpace(TwoFactorCode) ? null : TwoFactorCode.Trim(),
                localVaultKey,
                cancellationToken);
            RequiresTwoFactor = result.RequiresTwoFactor ||
                                (wasVerifyingTwoFactor && !result.Success);
            if (wasVerifyingTwoFactor && RequiresTwoFactor && !result.Success)
            {
                // The panel stays visible after a rejected code, so publish a
                // focus cue even though the Boolean state itself did not change.
                OnPropertyChanged(nameof(RequiresTwoFactor));
            }

            StatusMessage = BuildLoginStatusMessage(result, wasVerifyingTwoFactor);
            if (result.Success && result.Account is not null)
            {
                _ipatoolService.InvalidateAccountCache(account);
                var duplicate = Accounts.FirstOrDefault(existing =>
                    !ReferenceEquals(existing, account) &&
                    string.Equals(
                        existing.Email,
                        result.Account.Email,
                        StringComparison.OrdinalIgnoreCase));
                if (duplicate is not null)
                {
                    _ipatoolService.RemoveLocalAccountSession(account);
                    ForgetTransientLocalVaultState(account.Id);
                    if (!accountWasPersisted)
                    {
                        _pendingAccountId = Guid.NewGuid().ToString("N");
                    }

                    throw new InvalidOperationException(
                        $"{result.Account.Email} already belongs to another local account profile.");
                }

                account.Email = result.Account.Email;
                account.LocalVaultKey = localVaultKey;
                if (!Accounts.Contains(account))
                {
                    Accounts.Add(account);
                }

                if (_pendingAccountId is not null &&
                    !string.Equals(_pendingAccountId, account.Id, StringComparison.Ordinal))
                {
                    if (!RemovePendingAccountSession())
                    {
                        throw new IOException(
                            "The temporary account session could not be removed after selecting the existing profile.");
                    }
                }

                _pendingAccountId = null;
                IsAddingAccount = false;
                ApplySelectedAccount(account, clearSecrets: false);
                SetActiveAccount(result.Account);
                SyncAccountsToConfiguration();
                try
                {
                    await _configurationService.SaveAsync();
                }
                catch (Exception exception)
                {
                    if (!accountWasPersisted)
                    {
                        Accounts.Remove(account);
                        _pendingAccountId = account.Id;
                        var temporarySessionRemoved = RemovePendingAccountSession();
                        if (temporarySessionRemoved)
                        {
                            ForgetTransientLocalVaultState(account.Id);
                            _pendingAccountId = Guid.NewGuid().ToString("N");
                        }

                        IsAddingAccount = true;
                        ApplySelectedAccount(previousSelectedAccount, clearSecrets: false);
                        Email = requestedEmail;
                        SetActiveAccount(null);
                        StatusMessage = temporarySessionRemoved
                            ? $"Apple sign-in succeeded, but the new account profile could not be saved. The temporary local session was removed: {exception.Message}"
                            : $"Apple sign-in succeeded, but the new profile and temporary session could not be removed cleanly. Retry Cancel Adding Account: {exception.Message}";
                    }
                    else
                    {
                        account.Email = originalEmail;
                        account.LocalVaultKey = originalLocalVaultKey;
                        IpatoolAccountInfo? restoredActiveAccount = result.Account;
                        if (string.IsNullOrEmpty(originalLocalVaultKey))
                        {
                            var temporarySessionRemoved = TryRemoveLocalAccountSession(account);
                            restoredActiveAccount = null;
                            StatusMessage = temporarySessionRemoved
                                ? $"The account connected, but its generated key could not be saved. The temporary local session was removed: {exception.Message}"
                                : $"The account connected, but neither its generated key nor the temporary local session could be saved cleanly: {exception.Message}";
                        }
                        else
                        {
                            StatusMessage = $"The account connected, but updated profile metadata could not be saved: {exception.Message}";
                        }

                        ApplySelectedAccount(previousSelectedAccount, clearSecrets: false);
                        SetActiveAccount(restoredActiveAccount);
                    }

                    SyncAccountsToConfiguration();
                    ClearTransientLocalVaultKey();
                    _addActivity("Apple Account profile save failed", exception.Message, false);
                    return;
                }

                ForgetTransientLocalVaultState();
                StatusMessage = $"Connected {result.Account.Email}. Searches and purchases now use this account's App Store region.";
            }
            else
            {
                SetActiveAccount(null);
            }

            _addActivity("App Store sign-in", result.Message, result.Success);
        });
        if (!RequiresTwoFactor)
        {
            ApplePassword = string.Empty;
        }

        TwoFactorCode = string.Empty;
    }

    private async Task CheckAccountAsync()
    {
        var account = SelectedAccount;
        if (account is null)
        {
            return;
        }

        SetActiveAccount(null);
        await RunBusyAsync("Checking account…", async cancellationToken =>
        {
            var accountInfo = await _ipatoolService.GetStoredAccountAsync(
                account,
                RequireLocalVaultKey(account),
                cancellationToken);
            var matches = accountInfo is not null && string.Equals(
                accountInfo.Email,
                account.Email,
                StringComparison.OrdinalIgnoreCase);
            SetActiveAccount(matches ? accountInfo : null);
            StatusMessage = accountInfo is null
                ? $"No sign-in was found for {account.Email}. Sign in to create its isolated session."
                : matches
                    ? $"{account.Email} is ready. Searches use this account's App Store region."
                    : $"This profile contains a sign-in for {accountInfo.Email}. Reconnect {account.Email}.";
        });
    }

    private async Task SearchAsync()
    {
        var account = SelectedAccount;
        if (account is null)
        {
            return;
        }

        await RunBusyAsync("Searching the App Store…", async cancellationToken =>
        {
            var apps = await _ipatoolService.SearchAsync(
                account,
                SearchQuery.Trim(),
                RequireLocalVaultKey(account),
                cancellationToken);
            SearchResults.Clear();
            foreach (var app in apps)
            {
                SearchResults.Add(app);
            }

            SelectedApp = SearchResults.FirstOrDefault();
            StatusMessage = apps.Count == 0
                ? $"No matching iPhone apps were found in {account.Email}'s App Store region."
                : $"Results found: {apps.Count} for {account.Email}.";
            _addActivity(
                "App Store search",
                $"{account.Email} — {SearchQuery.Trim()} — Results: {apps.Count}",
                true);
        });
    }

    private async Task LoadVersionsAsync()
    {
        var app = SelectedApp;
        var account = SelectedAccount;
        if (app is null || account is null)
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
            await RunBusyAsync("Loading version identifiers…", async operationCancellationToken =>
            {
                using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellation.Token,
                    operationCancellationToken);
                IReadOnlyList<StoreAppVersion> versions;
                try
                {
                    versions = await _ipatoolService.ListVersionsAsync(
                        account,
                        app,
                        RequireLocalVaultKey(account),
                        progress,
                        linkedCancellation.Token);
                }
                catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
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
        if (SelectedApp is null || SelectedAccount is null)
        {
            return;
        }

        var app = SelectedApp;
        var account = SelectedAccount;
        await RunBusyAsync($"Downloading {app.Name}…", async cancellationToken =>
        {
            var path = await _ipatoolService.DownloadAsync(
                account,
                app,
                _configurationService.Current.DownloadDirectory,
                RequireLocalVaultKey(account),
                SelectedVersionIdentifier,
                cancellationToken: cancellationToken);
            StatusMessage = $"Download complete for {account.Email}: {Path.GetFileName(path)}";
            _addActivity(
                "IPA download complete",
                $"{account.Email} — {app.Name} — {Path.GetFileName(path)}",
                true);
            IpaDownloaded?.Invoke(this, path);
        });
    }

    private async Task RunBusyAsync(
        string title,
        Func<CancellationToken, Task> action)
    {
        using var operationCancellation = new CancellationTokenSource();
        _operationCancellation = operationCancellation;
        IsBusy = true;
        OperationTitle = title;
        try
        {
            await action(operationCancellation.Token);
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            StatusMessage = "App Store operation canceled.";
        }
        catch (Exception exception)
        {
            if (exception is IpatoolAccountSessionException)
            {
                SetActiveAccount(null);
            }

            StatusMessage = exception.Message;
            _addActivity("App Store operation failed", exception.Message, false);
        }
        finally
        {
            if (ReferenceEquals(_operationCancellation, operationCancellation))
            {
                _operationCancellation = null;
            }

            IsBusy = false;
            OperationTitle = string.Empty;
            CompleteLeaveStoreCleanup();
        }
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(SearchActionHelp));
        LoginCommand.NotifyCanExecuteChanged();
        CheckAccountCommand.NotifyCanExecuteChanged();
        SearchCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        LoadVersionsCommand.NotifyCanExecuteChanged();
        AddAccountCommand.NotifyCanExecuteChanged();
        CancelAccountEditCommand.NotifyCanExecuteChanged();
        RequestRemoveAccountCommand.NotifyCanExecuteChanged();
        CancelRemoveAccountCommand.NotifyCanExecuteChanged();
        ConfirmRemoveAccountCommand.NotifyCanExecuteChanged();
        CancelTwoFactorCommand.NotifyCanExecuteChanged();
    }

    private void NotifyAccountCollectionStateChanged()
    {
        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(CanSelectAccount));
        OnPropertyChanged(nameof(StorefrontSummary));
        OnPropertyChanged(nameof(AccountFormTitle));
        OnPropertyChanged(nameof(IsEmptyAccountPromptVisible));
        OnPropertyChanged(nameof(IsAccountSelectionSectionVisible));
        OnPropertyChanged(nameof(IsAccountFormVisible));
        NotifyCommands();
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
