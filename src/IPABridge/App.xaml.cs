using System.Buffers.Binary;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IPABridge.ViewModels;
using IPABridge.Views;

namespace IPABridge;

public partial class App : Application
{
    private const string WpfBindingSmokeArgument = "--wpf-binding-smoke";
    private const string WpfBindingSmokeResultVariable = "IPA_BRIDGE_WPF_SMOKE_RESULT";

    private bool _isWpfBindingSmokeTest;
    private Exception? _wpfBindingSmokeFailure;

    protected override void OnStartup(StartupEventArgs e)
    {
        _isWpfBindingSmokeTest = e.Args.Contains(WpfBindingSmokeArgument, StringComparer.Ordinal);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        if (_isWpfBindingSmokeTest)
        {
            // Keep the real application entry point while suppressing its visible startup window.
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
        }

        base.OnStartup(e);

        if (_isWpfBindingSmokeTest)
        {
            RunWpfBindingSmokeTest();
            return;
        }

        MainWindow = new MainWindow();
        MainWindow.Show();
    }

    private void RunWpfBindingSmokeTest()
    {
        var exitCode = 0;
        var result = "PASS";
        MainViewModel? viewModel = null;
        MainWindow? mainWindow = null;
        PrivacyDialog? privacyDialog = null;
        try
        {
            viewModel = new MainViewModel();
            Size[] layoutSizes =
            [
                new Size(654, 414),
                new Size(814, 506),
                new Size(1180, 760)
            ];
            var dashboardView = new DashboardView();
            var storeView = new StoreView();
            var settingsView = new SettingsView();
            FrameworkElement[] views =
            [
                dashboardView,
                storeView,
                new LibraryView(),
                new DevicesView(),
                settingsView
            ];

            foreach (var view in views)
            {
                view.DataContext = viewModel;
                view.ApplyTemplate();
                foreach (var layoutSize in layoutSizes)
                {
                    view.Measure(layoutSize);
                    view.Arrange(new Rect(layoutSize));
                    view.UpdateLayout();
                    VerifyLayout(view);
                }
            }

            VerifyFirstAccountFlow(storeView, viewModel);
            VerifyTwoFactorPanelLayout(storeView, viewModel);
            VerifyVerboseLoggingTipDismissal(storeView, viewModel.Store);
            mainWindow = new MainWindow();
            privacyDialog = new PrivacyDialog();
            VerifyPrivacySurfaces(mainWindow, privacyDialog);

            if (_wpfBindingSmokeFailure is not null)
            {
                throw new InvalidOperationException(
                    "A WPF page raised an unhandled binding exception.",
                    _wpfBindingSmokeFailure);
            }
        }
        catch (Exception exception)
        {
            exitCode = 1;
            result = exception.ToString();
        }
        finally
        {
            try
            {
                privacyDialog?.Close();
                mainWindow?.Close();
                viewModel?.Dispose();
            }
            catch (Exception exception)
            {
                exitCode = 1;
                result = exception.ToString();
            }

            try
            {
                WriteWpfBindingSmokeResult(result);
            }
            finally
            {
                Shutdown(exitCode);
            }
        }
    }

    private static void VerifyLayout(FrameworkElement view)
    {
        if (view is DashboardView dashboard)
        {
            var contentBounds = dashboard.HeroContent
                .TransformToAncestor(dashboard.HeroCard)
                .TransformBounds(new Rect(dashboard.HeroContent.RenderSize));
            var paddedTop = dashboard.HeroCard.Padding.Top;
            var paddedBottom = dashboard.HeroCard.ActualHeight - dashboard.HeroCard.Padding.Bottom;

            if (contentBounds.Top < paddedTop - 0.5 || contentBounds.Bottom > paddedBottom + 0.5)
            {
                throw new InvalidOperationException(
                    $"Dashboard hero content is clipped: content={contentBounds}, padded range={paddedTop:F1}-{paddedBottom:F1}.");
            }

            if (dashboard.PlatformBadge.ActualWidth + 0.5 <
                    dashboard.PlatformBadgeText.ActualWidth +
                    dashboard.PlatformBadge.Padding.Left +
                    dashboard.PlatformBadge.Padding.Right ||
                dashboard.PlatformBadge.ActualHeight + 0.5 <
                    dashboard.PlatformBadgeText.ActualHeight +
                    dashboard.PlatformBadge.Padding.Top +
                    dashboard.PlatformBadge.Padding.Bottom)
            {
                throw new InvalidOperationException(
                    "The platform badge does not expand to contain its complete label.");
            }
        }

        if (view is StoreView store)
        {
            VerifyEditorViewport(store.StoreSearchBox, "App Store search input");
            VerifyEditorViewport(store.AppleAccountEmailBox, "Apple Account email input");
            VerifyEditorViewport(store.ApplePasswordBox, "Apple Account password input");
            if (store.DataContext is not MainViewModel mainViewModel)
            {
                throw new InvalidOperationException("The App Store page has no main view model.");
            }

            if (mainViewModel.Store.HasAccounts)
            {
                if (store.AppleAccountSelector.ActualWidth <= 0 ||
                    store.AppleAccountSelector.ActualHeight < 44 ||
                    store.AddAppleAccountButton.ActualWidth < 44 ||
                    store.AddAppleAccountButton.ActualHeight < 44 ||
                    store.AddFirstAppleAccountButton.Visibility != Visibility.Collapsed)
                {
                    throw new InvalidOperationException(
                        "The saved Apple Account selector row is not available in the account card.");
                }
            }
            else if (!mainViewModel.Store.IsAddingAccount &&
                     (store.AppleAccountSelectorRow.Visibility != Visibility.Collapsed ||
                      store.AddFirstAppleAccountButton.Visibility != Visibility.Visible ||
                      store.AddFirstAppleAccountButton.ActualHeight < 64 ||
                      store.AccountFormPanel.Visibility != Visibility.Collapsed))
            {
                throw new InvalidOperationException(
                    "The first-account action does not replace the empty Apple Account selector.");
            }

            if (mainViewModel.Store.RequiresTwoFactor)
            {
                VerifyEditorViewport(store.TwoFactorCodeBox, "Apple verification code input");
            }
        }

        if (view is DevicesView devices)
        {
            if (devices.DataContext is not MainViewModel mainViewModel)
            {
                throw new InvalidOperationException("The Devices page has no main view model.");
            }

            var expectedVisibility = mainViewModel.Devices.IsDeviceScanAvailable
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (devices.RefreshDevicesButton.Visibility != expectedVisibility)
            {
                throw new InvalidOperationException(
                    "The connected-device refresh action does not match device readiness.");
            }

            if (expectedVisibility == Visibility.Visible &&
                (devices.RefreshDevicesButton.ActualWidth < 98 ||
                 devices.RefreshDevicesButton.ActualHeight < 44))
            {
                throw new InvalidOperationException(
                    "The connected-device refresh action has an undersized hit target.");
            }
        }

        if (view is SettingsView settings)
        {
            var localPreferencesBounds = settings.LocalPreferencesCard
                .TransformToAncestor(settings.SettingsScrollViewer)
                .TransformBounds(new Rect(settings.LocalPreferencesCard.RenderSize));
            if (localPreferencesBounds.Left < 13.5)
            {
                throw new InvalidOperationException(
                    "The Settings card does not reserve enough left-side space for its shadow.");
            }

            VerifyElementsDoNotOverlap(
                settings.SettingsStatusText,
                settings.SaveSettingsButton,
                settings.LocalPreferencesCard,
                "Settings status and Save Settings action");
            VerifyElementsDoNotOverlap(
                settings.SoftwareUpdateStatusText,
                settings.CheckForUpdatesButton,
                settings.SoftwareUpdateCard,
                "software-update status and action");
        }
    }

    private static void VerifyElementsDoNotOverlap(
        FrameworkElement leadingElement,
        FrameworkElement trailingElement,
        FrameworkElement ancestor,
        string description)
    {
        var leadingBounds = leadingElement
            .TransformToAncestor(ancestor)
            .TransformBounds(new Rect(leadingElement.RenderSize));
        var trailingBounds = trailingElement
            .TransformToAncestor(ancestor)
            .TransformBounds(new Rect(trailingElement.RenderSize));
        if (leadingBounds.Right > trailingBounds.Left + 0.5)
        {
            throw new InvalidOperationException(
                $"The {description} overlap: leading={leadingBounds}, trailing={trailingBounds}.");
        }
    }

    private static void VerifyEditorViewport(Control input, string description)
    {
        var contentHost = input.Template.FindName("PART_ContentHost", input) as ScrollViewer;
        if (contentHost is null)
        {
            throw new InvalidOperationException($"The {description} has no scrolling content host.");
        }

        if (contentHost.ViewportHeight + 0.5 < input.FontSize)
        {
            throw new InvalidOperationException(
                $"The {description} editor viewport is too short: " +
                $"viewport={contentHost.ViewportHeight:F1}, font={input.FontSize:F1}.");
        }
    }

    private static void VerifyFirstAccountFlow(StoreView storeView, MainViewModel mainViewModel)
    {
        var viewModel = mainViewModel.Store;
        if (viewModel.HasAccounts)
        {
            return;
        }

        var addCommand = storeView.AddFirstAppleAccountButton.Command;
        if (addCommand is null || !addCommand.CanExecute(null))
        {
            throw new InvalidOperationException("The empty account state has no executable Add action.");
        }

        addCommand.Execute(null);
        storeView.UpdateLayout();
        if (!viewModel.IsAddingAccount ||
            storeView.AppleAccountSelectionSection.Visibility != Visibility.Collapsed ||
            storeView.AccountFormPanel.Visibility != Visibility.Visible ||
            storeView.AppleAccountEmailBox.IsReadOnly)
        {
            throw new InvalidOperationException(
                "The first-account action did not replace the empty state with an editable credential form.");
        }

        if (mainViewModel.Settings.IsIpatoolAvailable)
        {
            if (storeView.IpatoolSignInPrerequisitePanel.Visibility != Visibility.Collapsed ||
                storeView.AccountCredentialsPanel.Visibility != Visibility.Visible ||
                storeView.AddAccountSignInButton.Visibility != Visibility.Visible ||
                storeView.ExistingAccountSessionActions.Visibility != Visibility.Collapsed)
            {
                throw new InvalidOperationException(
                    "The ready first-account flow does not expose one clear sign-in action.");
            }
        }
        else if (storeView.IpatoolSignInPrerequisitePanel.Visibility != Visibility.Visible ||
                 storeView.AccountCredentialsPanel.Visibility != Visibility.Collapsed ||
                 storeView.InstallIpatoolBeforeSignInButton.Command is not { } installCommand ||
                 !installCommand.CanExecute(null))
        {
            throw new InvalidOperationException(
                "The first-account flow does not replace unavailable sign-in with an executable ipatool prerequisite.");
        }

        var cancelCommand = viewModel.CancelAccountEditCommand;
        if (!cancelCommand.CanExecute(null))
        {
            throw new InvalidOperationException("The first-account form has no executable Cancel action.");
        }

        cancelCommand.Execute(null);
        storeView.UpdateLayout();
        if (viewModel.IsAddingAccount ||
            storeView.AddFirstAppleAccountButton.Visibility != Visibility.Visible ||
            storeView.AccountFormPanel.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException(
                "Canceling the first-account form did not restore the empty account action.");
        }
    }

    private static void VerifyVerboseLoggingTipDismissal(StoreView storeView, StoreViewModel viewModel)
    {
        if (storeView.VerboseLoggingTip.Visibility != Visibility.Visible)
        {
            throw new InvalidOperationException("The verbose logging tip is not visible by default.");
        }

        var dismissCommand = storeView.DismissVerboseLoggingTipButton.Command;
        if (dismissCommand is null || !dismissCommand.CanExecute(null))
        {
            throw new InvalidOperationException("The verbose logging tip has no executable dismiss command.");
        }

        dismissCommand.Execute(null);
        storeView.UpdateLayout();
        if (viewModel.IsVerboseLoggingTipVisible ||
            storeView.VerboseLoggingTip.Visibility != Visibility.Collapsed)
        {
            throw new InvalidOperationException("The verbose logging tip did not collapse after dismissal.");
        }
    }

    private static void VerifyTwoFactorPanelLayout(
        StoreView storeView,
        MainViewModel mainViewModel)
    {
        if (mainViewModel.Store.HasAccounts || Environment.ProcessPath is null)
        {
            return;
        }

        var originalIpatoolPath = mainViewModel.Settings.IpatoolPath;
        try
        {
            mainViewModel.Settings.IpatoolPath = Environment.ProcessPath;
            mainViewModel.Store.AddAccountCommand.Execute(null);
            mainViewModel.Store.Email = "layout@example.invalid";
            mainViewModel.Store.ApplePassword = "layout-only-secret";
            mainViewModel.Store.SearchQuery = "layout";
            typeof(StoreViewModel)
                .GetProperty(nameof(StoreViewModel.RequiresTwoFactor))!
                .SetValue(mainViewModel.Store, true);
            storeView.DataContext = null;
            storeView.DataContext = mainViewModel;
            var compactStoreSize = new Size(794, 444);
            storeView.Measure(compactStoreSize);
            storeView.Arrange(new Rect(compactStoreSize));
            storeView.UpdateLayout();

            if (storeView.AccountCredentialsPanel.Visibility != Visibility.Visible ||
                storeView.PrimaryAccountCredentialsPanel.Visibility != Visibility.Collapsed ||
                storeView.TwoFactorVerificationPanel.Visibility != Visibility.Visible ||
                storeView.TwoFactorCodeBox.ActualHeight < 52 ||
                storeView.TwoFactorVerificationPanel.ActualHeight < 180)
            {
                throw new InvalidOperationException(
                    "The two-factor challenge does not replace the credential form with a usable rounded panel.");
            }

            VerifyEditorViewport(storeView.TwoFactorCodeBox, "Apple verification code input");
            storeView.FocusTwoFactorVerificationInput();
            storeView.UpdateLayout();
            VerifyElementWithinVerticalViewport(
                storeView.TwoFactorBackButton,
                storeView.AppleAccountScrollViewer,
                "two-factor Back action");
            VerifyElementWithinVerticalViewport(
                storeView.TwoFactorVerifyButton,
                storeView.AppleAccountScrollViewer,
                "two-factor verification action");
            if (storeView.TwoFactorBackButton.ActualWidth < 64 ||
                storeView.TwoFactorVerifyButton.ActualWidth < 160 ||
                storeView.TwoFactorVerifyButton.ActualWidth <
                storeView.TwoFactorBackButton.ActualWidth + 90)
            {
                throw new InvalidOperationException(
                    "The two-factor actions do not reserve enough width for the complete verification label. " +
                    $"Back: {storeView.TwoFactorBackButton.ActualWidth:0.#}; " +
                    $"Verify: {storeView.TwoFactorVerifyButton.ActualWidth:0.#}.");
            }

            if (storeView.StoreSearchButton.IsEnabled ||
                !string.Equals(
                    storeView.StoreSearchButton.ToolTip as string,
                    "Finish Apple verification before searching.",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The disabled App Store search action does not explain its pending Apple verification prerequisite.");
            }

            mainViewModel.Store.TwoFactorCode = "12";
            storeView.TwoFactorCodeBox.SelectAll();
            var formattedCode = new DataObject();
            formattedCode.SetData(DataFormats.UnicodeText, "123 456\r\n");
            storeView.TwoFactorCodeBox.RaiseEvent(new DataObjectPastingEventArgs(
                formattedCode,
                isDragDrop: false,
                formatToApply: DataFormats.UnicodeText));
            if (storeView.TwoFactorCodeBox.Text != "123456" ||
                mainViewModel.Store.TwoFactorCode != "123456" ||
                !BindingOperations.IsDataBound(storeView.TwoFactorCodeBox, TextBox.TextProperty))
            {
                throw new InvalidOperationException(
                    "Formatted two-factor code paste was not normalized without breaking its binding.");
            }

            if (AutomationProperties.GetLiveSetting(storeView.StoreStatusText) !=
                    AutomationLiveSetting.Polite ||
                string.IsNullOrWhiteSpace(AutomationProperties.GetName(storeView.StoreStatusText)))
            {
                throw new InvalidOperationException(
                    "The App Store status is not exposed as a named polite live region.");
            }

            if (!mainViewModel.Store.CancelTwoFactorCommand.CanExecute(null))
            {
                throw new InvalidOperationException("The two-factor panel has no executable Back action.");
            }

            mainViewModel.Store.CancelTwoFactorCommand.Execute(null);
            storeView.UpdateLayout();
            if (storeView.TwoFactorVerificationPanel.Visibility != Visibility.Collapsed ||
                storeView.PrimaryAccountCredentialsPanel.Visibility != Visibility.Visible)
            {
                throw new InvalidOperationException(
                    "Leaving two-factor verification does not restore the credential form.");
            }

            mainViewModel.Store.CancelAccountEditCommand.Execute(null);
        }
        finally
        {
            mainViewModel.Settings.IpatoolPath = originalIpatoolPath;
        }
    }

    private static void VerifyElementWithinVerticalViewport(
        FrameworkElement element,
        ScrollViewer viewport,
        string description)
    {
        var bounds = element
            .TransformToAncestor(viewport)
            .TransformBounds(new Rect(element.RenderSize));
        if (bounds.Top < -0.5 || bounds.Bottom > viewport.ViewportHeight + 0.5)
        {
            throw new InvalidOperationException(
                $"The {description} is outside the compact account viewport: " +
                $"bounds={bounds}, viewport height={viewport.ViewportHeight:F1}.");
        }
    }

    private static void VerifyPrivacySurfaces(MainWindow mainWindow, PrivacyDialog privacyDialog)
    {
        mainWindow.ApplyTemplate();
        VerifyBrandAssets(mainWindow, privacyDialog);
        if (!mainWindow.PrivacyCardButton.Focusable ||
            string.IsNullOrWhiteSpace(AutomationProperties.GetName(mainWindow.PrivacyCardButton)))
        {
            throw new InvalidOperationException(
                "The privacy card is not keyboard focusable or has no automation name.");
        }

        if (!privacyDialog.DoneButton.IsDefault ||
            !privacyDialog.DoneButton.Focusable ||
            !privacyDialog.CloseDialogButton.Focusable)
        {
            throw new InvalidOperationException("The privacy dialog has no accessible close path.");
        }

        if (privacyDialog.Content is not FrameworkElement content)
        {
            throw new InvalidOperationException("The privacy dialog has no layout root.");
        }

        Size[] dialogLayoutSizes =
        [
            new Size(420, 360),
            new Size(500, 520),
            new Size(570, 640)
        ];
        foreach (var layoutSize in dialogLayoutSizes)
        {
            content.Measure(layoutSize);
            content.Arrange(new Rect(layoutSize));
            content.UpdateLayout();
            if (privacyDialog.PrivacyScrollViewer.RenderSize.Height <= 0 ||
                privacyDialog.DoneButton.RenderSize.Height <= 0)
            {
                throw new InvalidOperationException(
                    $"The privacy dialog does not lay out at {layoutSize.Width}x{layoutSize.Height}.");
            }
        }
    }

    private static void VerifyBrandAssets(MainWindow mainWindow, PrivacyDialog privacyDialog)
    {
        if (mainWindow.BrandIconImage.Source is not BitmapSource brandIcon ||
            brandIcon.PixelWidth != 1024 ||
            brandIcon.PixelHeight != 1024)
        {
            throw new InvalidOperationException(
                "The main-window brand icon is not the embedded 1024x1024 bitmap asset.");
        }

        if (mainWindow.Icon is null || privacyDialog.Icon is null)
        {
            throw new InvalidOperationException(
                "The main window and privacy dialog must both load the application icon.");
        }

        var resource = GetResourceStream(
            new Uri("pack://application:,,,/Assets/IPA-Bridge.ico", UriKind.Absolute));
        if (resource is null)
        {
            throw new InvalidOperationException("The embedded application icon resource is unavailable.");
        }

        using var iconStream = resource.Stream;
        using var iconBuffer = new MemoryStream();
        iconStream.CopyTo(iconBuffer);
        VerifyApplicationIconFrames(iconBuffer.ToArray());
    }

    private static void VerifyApplicationIconFrames(byte[] iconBytes)
    {
        const int iconDirectoryHeaderLength = 6;
        const int iconDirectoryEntryLength = 16;
        int[] expectedSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
        byte[] pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        byte[] imageHeaderChunkType = [(byte)'I', (byte)'H', (byte)'D', (byte)'R'];

        if (iconBytes.Length < iconDirectoryHeaderLength ||
            BinaryPrimitives.ReadUInt16LittleEndian(iconBytes.AsSpan(0, 2)) != 0 ||
            BinaryPrimitives.ReadUInt16LittleEndian(iconBytes.AsSpan(2, 2)) != 1)
        {
            throw new InvalidOperationException("The embedded application icon has an invalid ICO header.");
        }

        var frameCount = BinaryPrimitives.ReadUInt16LittleEndian(iconBytes.AsSpan(4, 2));
        if (frameCount != expectedSizes.Length)
        {
            throw new InvalidOperationException(
                $"The embedded application icon has {frameCount} frames instead of {expectedSizes.Length}.");
        }

        var directoryLength = iconDirectoryHeaderLength + (frameCount * iconDirectoryEntryLength);
        if (iconBytes.Length < directoryLength)
        {
            throw new InvalidOperationException("The embedded application icon directory is truncated.");
        }

        var actualSizes = new HashSet<int>();
        var payloadRanges = new List<(long Start, long End)>();
        for (var frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            var entryOffset = iconDirectoryHeaderLength + (frameIndex * iconDirectoryEntryLength);
            var width = iconBytes[entryOffset] == 0 ? 256 : iconBytes[entryOffset];
            var height = iconBytes[entryOffset + 1] == 0 ? 256 : iconBytes[entryOffset + 1];
            var bitsPerPixel = BinaryPrimitives.ReadUInt16LittleEndian(iconBytes.AsSpan(entryOffset + 6, 2));
            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(iconBytes.AsSpan(entryOffset + 8, 4));
            var payloadOffset = BinaryPrimitives.ReadUInt32LittleEndian(iconBytes.AsSpan(entryOffset + 12, 4));

            if (width != height || !expectedSizes.Contains(width) || !actualSizes.Add(width))
            {
                throw new InvalidOperationException(
                    $"The embedded application icon has an unexpected or duplicate {width}x{height} frame.");
            }

            if (bitsPerPixel != 32)
            {
                throw new InvalidOperationException(
                    $"The embedded application icon's {width}x{height} frame is not 32-bit.");
            }

            var payloadStart = (long)payloadOffset;
            var payloadEnd = payloadStart + payloadLength;
            if (payloadStart < directoryLength ||
                payloadLength < 33 ||
                payloadEnd > iconBytes.Length ||
                payloadRanges.Any(range => payloadStart < range.End && range.Start < payloadEnd))
            {
                throw new InvalidOperationException(
                    $"The embedded application icon's {width}x{height} frame has an invalid payload range.");
            }

            payloadRanges.Add((payloadStart, payloadEnd));
            var payloadIndex = checked((int)payloadStart);
            if (!iconBytes.AsSpan(payloadIndex, pngSignature.Length).SequenceEqual(pngSignature) ||
                BinaryPrimitives.ReadUInt32BigEndian(iconBytes.AsSpan(payloadIndex + 8, 4)) != 13 ||
                !iconBytes.AsSpan(payloadIndex + 12, imageHeaderChunkType.Length).SequenceEqual(imageHeaderChunkType))
            {
                throw new InvalidOperationException(
                    $"The embedded application icon's {width}x{height} frame is not a valid PNG image.");
            }

            var pngWidth = BinaryPrimitives.ReadUInt32BigEndian(iconBytes.AsSpan(payloadIndex + 16, 4));
            var pngHeight = BinaryPrimitives.ReadUInt32BigEndian(iconBytes.AsSpan(payloadIndex + 20, 4));
            if (pngWidth != width || pngHeight != height)
            {
                throw new InvalidOperationException(
                    $"The embedded application icon frame declares {width}x{height} but contains a {pngWidth}x{pngHeight} PNG.");
            }
        }

        if (!actualSizes.SetEquals(expectedSizes))
        {
            throw new InvalidOperationException("The embedded application icon is missing one or more required frame sizes.");
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (_isWpfBindingSmokeTest)
        {
            _wpfBindingSmokeFailure ??= e.Exception;
            e.Handled = true;
            return;
        }

        MessageBox.Show(
            $"IPA Bridge encountered an unhandled error:\n\n{e.Exception.Message}",
            "IPA Bridge",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void WriteWpfBindingSmokeResult(string result)
    {
        var resultPath = Environment.GetEnvironmentVariable(WpfBindingSmokeResultVariable);
        if (!string.IsNullOrWhiteSpace(resultPath))
        {
            File.WriteAllText(resultPath, result);
        }
    }
}
