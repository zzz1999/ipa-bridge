using System.Collections.ObjectModel;
using IPABridge.Infrastructure;
using IPABridge.Models;
using IPABridge.Services;

namespace IPABridge.ViewModels;

public sealed class LibraryViewModel : ObservableObject
{
    private readonly ConfigurationService _configurationService;
    private readonly IpaLibraryService _ipaLibraryService;
    private bool _isRefreshing;
    private string _statusMessage = "Downloaded IPAs will appear here.";

    public LibraryViewModel(
        ConfigurationService configurationService,
        IpaLibraryService ipaLibraryService)
    {
        _configurationService = configurationService;
        _ipaLibraryService = ipaLibraryService;
        RefreshCommand = new AsyncRelayCommand(
            RefreshAsync,
            () => !IsRefreshing,
            exception => ReportError("Could not refresh the IPA library", exception));
    }

    public ObservableCollection<LocalIpa> Items { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set
        {
            if (SetProperty(ref _isRefreshing, value))
            {
                RefreshCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            var items = await _ipaLibraryService.ScanAsync(_configurationService.Current.DownloadDirectory);
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }

            StatusMessage = items.Count == 0
                ? "There are no IPAs in the current download folder."
                : $"IPA files: {items.Count}.";
        }
        catch (Exception exception)
        {
            ReportError("Could not refresh the IPA library", exception);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    public void ReportError(string operation, Exception exception)
    {
        StatusMessage = $"{operation}: {exception.Message}";
    }
}
