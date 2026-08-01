using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using IPABridge.Models;
using IPABridge.ViewModels;

namespace IPABridge.Views;

public partial class StoreView : UserControl
{
    private StoreViewModel? _storeViewModel;
    private SettingsViewModel? _settingsViewModel;

    public StoreView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) =>
        {
            _storeViewModel?.LeaveStore();
            DetachViewModel();
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        DetachViewModel();
        if (e.NewValue is MainViewModel mainViewModel)
        {
            _storeViewModel = mainViewModel.Store;
            _storeViewModel.PropertyChanged += StoreViewModelOnPropertyChanged;
            _settingsViewModel = mainViewModel.Settings;
            _settingsViewModel.PropertyChanged += SettingsViewModelOnPropertyChanged;
            FocusAccountEntryTarget(mainViewModel);
        }
    }

    private void DetachViewModel()
    {
        if (_storeViewModel is not null)
        {
            _storeViewModel.PropertyChanged -= StoreViewModelOnPropertyChanged;
            _storeViewModel = null;
        }

        if (_settingsViewModel is not null)
        {
            _settingsViewModel.PropertyChanged -= SettingsViewModelOnPropertyChanged;
            _settingsViewModel = null;
        }
    }

    private void StoreViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not StoreViewModel viewModel)
        {
            return;
        }

        // PasswordBox has no bindable Password property, so secrets stay in memory and are cleared from both layers together.
        if (e.PropertyName == nameof(StoreViewModel.ApplePassword) &&
            string.IsNullOrEmpty(viewModel.ApplePassword) &&
            ApplePasswordBox.Password.Length > 0)
        {
            ApplePasswordBox.Clear();
        }

        if (e.PropertyName == nameof(StoreViewModel.TwoFactorCode) &&
            string.IsNullOrEmpty(viewModel.TwoFactorCode) &&
            TwoFactorCodeBox.Password.Length > 0)
        {
            TwoFactorCodeBox.Clear();
        }

        if (e.PropertyName == nameof(StoreViewModel.VaultPassphrase) &&
            string.IsNullOrEmpty(viewModel.VaultPassphrase) &&
            VaultPassphraseBox.Password.Length > 0)
        {
            VaultPassphraseBox.Clear();
        }

        if (e.PropertyName == nameof(StoreViewModel.IsAddingAccount) && viewModel.IsAddingAccount)
        {
            FocusAccountEntryTarget();
        }
    }

    private void SettingsViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.IsIpatoolAvailable))
        {
            FocusAccountEntryTarget();
        }
    }

    private void FocusAccountEntryTarget(MainViewModel? mainViewModel = null)
    {
        var resolvedViewModel = mainViewModel ?? DataContext as MainViewModel;
        if (resolvedViewModel?.Store.IsAddingAccount != true)
        {
            return;
        }

        // The Add action replaces its own row. Focus either the prerequisite or the first credential field.
        Dispatcher.BeginInvoke(() =>
        {
            Control target = resolvedViewModel.Settings.IsIpatoolAvailable
                ? AppleAccountEmailBox
                : InstallIpatoolBeforeSignInButton;
            target.BringIntoView();
            target.Focus();
        });
    }

    private void ApplePasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.Store.ApplePassword = ApplePasswordBox.Password;
        }
    }

    private void TwoFactorCodeBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.Store.TwoFactorCode = TwoFactorCodeBox.Password;
        }
    }

    private void VaultPassphraseBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.Store.VaultPassphrase = VaultPassphraseBox.Password;
        }
    }

    private async void AppleAccountSelector_OnSelectionChanged(
        object sender,
        SelectionChangedEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel &&
            AppleAccountSelector.SelectedItem is AppleAccountProfile account)
        {
            await viewModel.Store.SelectAccountAsync(account);
        }
    }
}
