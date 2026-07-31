using System.Windows;
using System.Windows.Controls;
using IPABridge.ViewModels;
using Microsoft.Win32;

namespace IPABridge.Views;

public partial class DevicesView : UserControl
{
    public DevicesView()
    {
        InitializeComponent();
    }

    private void ChooseIpa_OnClick(object sender, RoutedEventArgs e)
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
        if (dialog.ShowDialog() == true)
        {
            viewModel.Devices.SelectedIpaPath = dialog.FileName;
        }
    }
}
