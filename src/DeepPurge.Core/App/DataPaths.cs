namespace DeepPurge.Core.App;

/// <summary>
/// Single source of truth for where DeepPurge stores per-user data.
///
/// Portable mode: if a file named <c>DeepPurge.portable</c> exists next to the
/// running exe, all state is redirected to <c>./Data/</c> beside the binary
/// (BCU pattern). Enables USB-stick / field deployment by sysadmins.
///
/// Installed mode: falls back to <c>%LocalAppData%\DeepPurge\</c>.
///
/// Resolution happens once per process via <see cref="Lazy{T}"/> — flipping
/// the marker file after launch requires a restart, which keeps the data
/// path stable for the life of the session.
/// </summary>
public static class DataPaths
{
    private const string PortableMarker = "DeepPurge.portable";

    private static readonly Lazy<string> _root = new(ResolveRoot);
    private static readonly Lazy<bool>   _portable = new(DetectPortable);

    public static bool   IsPortable => _portable.Value;
    public static string Root       => _root.Value;

    public static string Logs      => Ensure(Path.Combine(Root, "Logs"));
    public static string Backups   => Ensure(Path.Combine(Root, "Backups"));
    public static string Snapshots => Ensure(Path.Combine(Root, "Snapshots"));
    public static string Cleaners  => Ensure(Path.Combine(Root, "Cleaners"));
    public static string Config    => Ensure(Path.Combine(Root, "Config"));

    public static string ThemeFile    => Path.Combine(Config, "theme.txt");
    public static string SettingsFile => Path.Combine(Config, "settings.json");

    /// <summary>Create the portable marker next to the running exe.</summary>
    /// <exception cref="IOException">
    /// Thrown when the exe directory is read-only (e.g. Program Files without
    /// elevation). Callers should surface this to the user rather than silently
    /// succeeding with no effect.
    /// </exception>
    public static void EnablePortable()
    {
        var dir = AppContext.BaseDirectory;
        var path = Path.Combine(dir, PortableMarker);
        // Deliberately let IOException / UnauthorizedAccessException propagate.
        File.WriteAllText(path, "This file enables portable mode. Delete to use %LocalAppData%.\r\n");
    }

    public static bool TryEnablePortable(out string? errorMessage)
    {
        try { EnablePortable(); errorMessage = null; return true; }
        catch (Exception ex) { errorMessage = ex.Message; return false; }
    }

    // ═══════════════════════════════════════════════════════

    private static bool DetectPortable()
    {
        try
        {
            var exeDir = AppContext.BaseDirectory;
            return File.Exists(Path.Combine(exeDir, PortableMarker));
        }
        catch { return false; }
    }

    private static string ResolveRoot()
    {
        if (_portable.Value)
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Data");
            Directory.CreateDirectory(dir);
            return dir;
        }
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(appData, "DeepPurge");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string Ensure(string path)
    {
        try { Directory.CreateDirectory(path); } catch { }
        return path;
    }
}
