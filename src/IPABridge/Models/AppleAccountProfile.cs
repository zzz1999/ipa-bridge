using System.Text.Json.Serialization;
using IPABridge.Infrastructure;

namespace IPABridge.Models;

public sealed class AppleAccountProfile : ObservableObject
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _email = string.Empty;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Email
    {
        get => _email;
        set
        {
            if (SetProperty(ref _email, value))
            {
                OnPropertyChanged(nameof(DisplayLabel));
                OnPropertyChanged(nameof(Monogram));
            }
        }
    }

    [JsonIgnore]
    public string DisplayLabel => Email;

    [JsonIgnore]
    public string Monogram
    {
        get
        {
            var trimmedEmail = Email.Trim();
            return trimmedEmail.Length == 0
                ? "?"
                : char.ToUpperInvariant(trimmedEmail[0]).ToString();
        }
    }
}
