namespace UZConnect.Windows;

/// <summary>
/// Locates the bundled helper files. Folder builds use the Assets directory next to the
/// executable; a single-file build carries the copies inside the assembly and extracts them
/// to the local data directory on first use.
/// </summary>
public static class HelperFiles
{
    private const string ResourcePrefix = "UZConnect.Helpers.";
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string> Resolved = new(StringComparer.OrdinalIgnoreCase);

    public static string Resolve(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains(Path.DirectorySeparatorChar) ||
            fileName.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new ArgumentException("A plain helper file name is required.", nameof(fileName));
        }

        lock (Gate)
        {
            if (Resolved.TryGetValue(fileName, out var cached) && File.Exists(cached))
            {
                return cached;
            }

            var beside = Path.Combine(AppContext.BaseDirectory, "Assets", fileName);
            if (File.Exists(beside))
            {
                return Resolved[fileName] = beside;
            }

            var directory = Path.Combine(AppPaths.Root, "helpers");
            Directory.CreateDirectory(directory);
            using var resource = typeof(HelperFiles).Assembly
                .GetManifestResourceStream(ResourcePrefix + fileName);
            if (resource is null)
            {
                throw new FileNotFoundException($"The bundled helper '{fileName}' is missing.", fileName);
            }

            // Always replace, so an app update never reuses a stale helper.
            var target = Path.Combine(directory, fileName);
            var temporary = target + ".tmp";
            using (var output = File.Create(temporary))
            {
                resource.CopyTo(output);
            }

            File.Move(temporary, target, overwrite: true);
            return Resolved[fileName] = target;
        }
    }
}
