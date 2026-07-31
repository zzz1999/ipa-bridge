using IPABridge.Models;

namespace IPABridge.Services;

public sealed class IpaLibraryService
{
    public Task<IReadOnlyList<LocalIpa>> ScanAsync(string directory)
    {
        return Task.Run<IReadOnlyList<LocalIpa>>(() =>
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return [];
            }

            return Directory.EnumerateFiles(directory, "*.ipa", SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTime)
                .Select(file => new LocalIpa
                {
                    FilePath = file.FullName,
                    Name = Path.GetFileNameWithoutExtension(file.Name),
                    SizeBytes = file.Length,
                    ModifiedAt = file.LastWriteTime
                })
                .ToArray();
        });
    }
}
