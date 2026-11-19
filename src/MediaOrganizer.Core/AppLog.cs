namespace MediaOrganizer.Core;

/// <summary>
/// Simple thread-safe file logger (%LocalAppData%\MediaOrganizer\logs).
/// Never logs GPS coordinates. File paths are logged because they are needed for troubleshooting.
/// </summary>
public static class AppLog
{
    private static readonly object Gate = new();

    public static string LogDirectory { get; set; } = Path.Combine(OrganizerSettings.AppDataFolder, "logs");

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                var file = Path.Combine(LogDirectory, $"app-{DateTime.Now:yyyyMMdd}.log");
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                if (ex != null) line += $" | {ex.GetType().Name}: {ex.Message}";
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never crash the application.
        }
    }
}
