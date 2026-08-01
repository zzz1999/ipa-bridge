using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
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
        DataObject.AddPastingHandler(TwoFactorCodeBox, TwoFactorCodeBox_OnPaste);
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

        // PasswordBox has no bindable Password property, so the Apple password
        // stays in memory and is cleared from both layers together.
        if (e.PropertyName == nameof(StoreViewModel.ApplePassword) &&
            string.IsNullOrEmpty(viewModel.ApplePassword) &&
            ApplePasswordBox.Password.Length > 0)
        {
            ApplePasswordBox.Clear();
        }

        if (e.PropertyName == nameof(StoreViewModel.RequiresTwoFactor))
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsLoaded)
                {
                    return;
                }

                if (viewModel.RequiresTwoFactor)
                {
                    FocusTwoFactorVerificationInput();
                }
                else
                {
                    ApplePasswordBox.BringIntoView();
                    ApplePasswordBox.Focus();
                }
            });
        }

        if (e.PropertyName == nameof(StoreViewModel.StatusMessage))
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsLoaded)
                {
                    return;
                }

                var peer = UIElementAutomationPeer.FromElement(StoreStatusText) ??
                           UIElementAutomationPeer.CreatePeerForElement(StoreStatusText);
                peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            });
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
            if (!IsLoaded)
            {
                return;
            }

            Control target = resolvedViewModel.Settings.IsIpatoolAvailable
                ? AppleAccountEmailBox
                : InstallIpatoolBeforeSignInButton;
            target.BringIntoView();
            target.Focus();
        });
    }

    internal void FocusTwoFactorVerificationInput()
    {
        TwoFactorCodeBox.Focus();
        TwoFactorCodeBox.SelectAll();
        // The help block can make the panel taller than a compact viewport. Keep
        // the verification action visible after focus scrolls to the code box.
        TwoFactorVerifyButton.BringIntoView();
    }

    private void ApplePasswordBox_OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.Store.ApplePassword = ApplePasswordBox.Password;
        }
    }

    private void TwoFactorCodeBox_OnPreviewTextInput(
        object sender,
        TextCompositionEventArgs eventArgs)
    {
        eventArgs.Handled = !IsAsciiDigits(eventArgs.Text) ||
                            TwoFactorCodeBox.Text.Length -
                            TwoFactorCodeBox.SelectionLength +
                            eventArgs.Text.Length > 6;
    }

    private void TwoFactorCodeBox_OnPaste(object sender, DataObjectPastingEventArgs eventArgs)
    {
        if (!eventArgs.DataObject.GetDataPresent(DataFormats.UnicodeText) ||
            eventArgs.DataObject.GetData(DataFormats.UnicodeText) is not string pastedText)
        {
            eventArgs.CancelCommand();
            return;
        }

        var normalizedText = new string(pastedText
            .Where(character => character is >= '0' and <= '9')
            .ToArray());
        var resultingLength = TwoFactorCodeBox.Text.Length -
                              TwoFactorCodeBox.SelectionLength +
                              normalizedText.Length;
        if (normalizedText.Length == 0 || resultingLength > 6)
        {
            eventArgs.CancelCommand();
            return;
        }

        if (!string.Equals(normalizedText, pastedText, StringComparison.Ordinal))
        {
            var selectionStart = TwoFactorCodeBox.SelectionStart;
            var updatedText = TwoFactorCodeBox.Text
                .Remove(selectionStart, TwoFactorCodeBox.SelectionLength)
                .Insert(selectionStart, normalizedText);

            eventArgs.CancelCommand();
            TwoFactorCodeBox.SetCurrentValue(TextBox.TextProperty, updatedText);
            TwoFactorCodeBox.CaretIndex = selectionStart + normalizedText.Length;
        }
    }

    private static bool IsAsciiDigits(string value)
    {
        return value.Length > 0 &&
               value.All(character => character is >= '0' and <= '9');
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
