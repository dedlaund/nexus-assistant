using System.Text.Json;

namespace NexusAssistant.Services;

public sealed class LocalStore(string directory)
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };
    public string DirectoryPath { get; } = directory;

    public async Task<T> ReadAsync<T>(string file, T fallback)
    {
        var path = Path.Combine(DirectoryPath, file);
        if (!File.Exists(path)) return fallback;
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream) ?? fallback;
    }

    public async Task WriteAsync<T>(string file, T value)
    {
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, file);
        var temporary = Path.Combine(DirectoryPath, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(stream, value, Format);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
