using System.IO;
using System.Windows;
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
            var layoutSize = new Size(1180, 760);
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
                view.Measure(layoutSize);
                view.Arrange(new Rect(layoutSize));
                view.UpdateLayout();
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
