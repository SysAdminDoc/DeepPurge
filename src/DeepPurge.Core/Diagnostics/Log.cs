namespace DeepPurge.Core.Diagnostics;

/// <summary>
/// Append-only lightweight logger that routes through <see cref="App.DataPaths"/>
/// so it respects portable mode. Never throws — logging must not crash
/// production callers. Use this to capture exceptions that were previously
/// being swallowed silently, so we have a paper trail when something
/// goes wrong in the field.
/// </summary>
public static class Log
{
    private static readonly object _lock = new();
    private const long MaxBytes = 5L * 1024 * 1024; // 5 MB → rotate

    public static void Info(string message)   => Write("INFO",  message, null);
    public static void Warn(string message)   => Write("WARN",  message, null);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var path = Path.Combine(App.DataPaths.Logs, "deeppurge.log");
            lock (_lock)
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    // Rotate: rename → .1. Keep at most one previous log.
                    var rotated = path + ".1";
                    try { if (File.Exists(rotated)) File.Delete(rotated); } catch { }
                    try { File.Move(path, rotated); } catch { }
                }
                using var sw = new StreamWriter(path, append: true, encoding: System.Text.Encoding.UTF8);
                sw.Write($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}");
                if (ex != null) sw.Write($" | {ex.GetType().Name}: {ex.Message}");
                sw.WriteLine();
            }
        }
        catch { /* logging must never crash callers */ }
    }
}
