namespace IPABridge.Infrastructure;

public static class AppPaths
{
    public static string LocalDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IPA Bridge");

    public static string ConfigurationFile => Path.Combine(LocalDataDirectory, "settings.secure.json");

    public static string LegacyConfigurationFile => Path.Combine(LocalDataDirectory, "settings.json");

    public static string LocalDataKeyFile => Path.Combine(LocalDataDirectory, "master-key.v1");

    public static string ToolsDirectory => Path.Combine(LocalDataDirectory, "Tools");

    public static string IpatoolDirectory => Path.Combine(ToolsDirectory, "ipatool");

    public static string IpatoolAccountsDirectory => Path.Combine(LocalDataDirectory, "Accounts");

    public static string DeviceToolsDirectory => Path.Combine(ToolsDirectory, "idevice");

    public static string TemporaryDirectory => Path.Combine(LocalDataDirectory, "Temporary");

    public static string DefaultDownloadDirectory
    {
        get
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(userProfile, "Downloads", "IPA Bridge");
        }
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(LocalDataDirectory);
        Directory.CreateDirectory(ToolsDirectory);
        Directory.CreateDirectory(TemporaryDirectory);
        Directory.CreateDirectory(DefaultDownloadDirectory);
    }
}
