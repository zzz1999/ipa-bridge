using System.Windows;
using System.Windows.Controls;
using IPABridge.ViewModels;
using Microsoft.Win32;

namespace IPABridge.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private async void ChooseIpatool_OnClick(object sender, RoutedEventArgs e) =>
        await RunUiOperationAsync("Could not select ipatool", async viewModel =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select ipatool.exe",
                Filter = "ipatool (ipatool*.exe)|ipatool*.exe|Executable Files (*.exe)|*.exe",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            viewModel.Settings.IpatoolPath = dialog.FileName;
            await viewModel.Settings.ApplySelectedPathsAsync();
        });

    private async void ChooseDeviceTools_OnClick(object sender, RoutedEventArgs e) =>
        await RunUiOperationAsync("Could not select the iOS device tools", async viewModel =>
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select the folder containing idevice-tools.exe or ideviceinstaller.exe",
                Multiselect = false
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            viewModel.Settings.DeviceToolsDirectory = dialog.FolderName;
            await viewModel.Settings.ApplySelectedPathsAsync();
        });

    private async void ChooseDownloadDirectory_OnClick(object sender, RoutedEventArgs e) =>
        await RunUiOperationAsync("Could not select the IPA download folder", async viewModel =>
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select the IPA download folder",
                Multiselect = false
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            viewModel.Settings.DownloadDirectory = dialog.FolderName;
            await viewModel.Settings.ApplySelectedPathsAsync();
            await viewModel.Library.RefreshAsync();
        });

    private async Task RunUiOperationAsync(
        string operation,
        Func<MainViewModel, Task> action)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            await action(viewModel);
        }
        catch (Exception exception)
        {
            viewModel.Settings.ReportError(operation, exception);
        }
    }
}
