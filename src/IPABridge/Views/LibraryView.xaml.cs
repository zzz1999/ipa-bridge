using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using IPABridge.Models;
using IPABridge.ViewModels;
using Microsoft.Win32;

namespace IPABridge.Views;

public partial class LibraryView : UserControl
{
    public LibraryView()
    {
        InitializeComponent();
    }

    private void ChooseExternalIpa_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select an IPA to install",
            Filter = "iOS App Package (*.ipa)|*.ipa",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        viewModel.Devices.SelectedIpaPath = dialog.FileName;
        viewModel.CurrentPage = NavigationPage.Devices;
    }

    private void OpenDownloadDirectory_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        try
        {
            var directory = viewModel.Settings.DownloadDirectory;
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            viewModel.Library.ReportError("Could not open the IPA download folder", exception);
        }
    }
}
