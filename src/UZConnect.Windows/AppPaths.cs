namespace UZConnect.Windows;

public static class AppPaths
{
    private static readonly Lazy<string> RootLazy = new(() =>
    {
        var overridePath = Environment.GetEnvironmentVariable("UZCONNECT_DATA_DIR");
        var root = string.IsNullOrWhiteSpace(overridePath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UZConnect")
            : overridePath;
        Directory.CreateDirectory(root);
        return root;
    });

    public static string Root => RootLazy.Value;
    public static string DatabasePath => Path.Combine(Root, "uzconnect.db");
    public static string LogsDirectory => Ensure(Path.Combine(Root, "logs"));
    public static string ExportsDirectory => Ensure(Path.Combine(Root, "exports"));
    public static string AgentResultFallbackDirectory => Ensure(Path.Combine(Root, "agent-results"));

    private static string Ensure(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}
