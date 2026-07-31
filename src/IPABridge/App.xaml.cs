using System.IO;
using System.Windows;
using System.Windows.Controls;
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
        try
        {
            viewModel = new MainViewModel();
            Size[] layoutSizes =
            [
                new Size(654, 414),
                new Size(814, 506),
                new Size(1180, 760)
            ];
            FrameworkElement[] views =
            [
                new DashboardView(),
                new StoreView(),
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
            VerifyEditorViewport(store.ApplePasswordBox, "Apple Account password input");
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
