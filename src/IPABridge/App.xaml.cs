using System.Buffers.Binary;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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
            var storeView = new StoreView();
            FrameworkElement[] views =
            [
                new DashboardView(),
                storeView,
                new LibraryView(),
                new DevicesView(),
                new SettingsView()
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

            VerifyFirstAccountFlow(storeView, viewModel.Store);
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
        }

        if (view is StoreView store)
        {
            VerifyEditorViewport(store.StoreSearchBox, "App Store search input");
            VerifyEditorViewport(store.AppleAccountEmailBox, "Apple Account email input");
            VerifyEditorViewport(store.ApplePasswordBox, "Apple Account password input");
            VerifyEditorViewport(store.VaultPassphraseBox, "local vault passphrase input");
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

    private static void VerifyFirstAccountFlow(StoreView storeView, StoreViewModel viewModel)
    {
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
