using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using IPABridge.ViewModels;

namespace IPABridge.Views;

public partial class StoreView : UserControl
{
    private StoreViewModel? _storeViewModel;

    public StoreView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) =>
        {
            _storeViewModel?.ClearSecrets();
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
        }
    }

    private void DetachViewModel()
    {
        if (_storeViewModel is not null)
        {
            _storeViewModel.PropertyChanged -= StoreViewModelOnPropertyChanged;
            _storeViewModel = null;
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
}
