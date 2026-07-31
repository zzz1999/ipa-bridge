using System.ComponentModel;
using System.Windows;
using IPABridge.Infrastructure;
using IPABridge.ViewModels;
using IPABridge.Views;

namespace IPABridge;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        SourceInitialized += (_, _) => WindowBackdropService.Apply(this);
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _viewModel.InitializeAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"Initialization failed:\n\n{exception.Message}",
                "IPA Bridge",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e) => _viewModel.Dispose();

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void PrivacyCardButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new PrivacyDialog
        {
            Owner = this
        };
        _ = dialog.ShowDialog();
    }
}
