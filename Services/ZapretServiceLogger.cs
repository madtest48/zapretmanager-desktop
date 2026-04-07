using System.Text;

namespace ZapretManager.Services;

internal static class ZapretServiceLogger
{
    private static readonly object Sync = new();
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ZapretManager",
        "Logs");
    private static readonly string LogPath = Path.Combine(LogDirectory, "service-start.log");
    private static readonly string BackupLogPath = Path.Combine(LogDirectory, "service-start.previous.log");
    private const long MaxLogSizeBytes = 512 * 1024;

    public static string CurrentLogPath => LogPath;

    public static void Info(string message) => Write("INFO", message);
    public static void Warning(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded();

                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(LogPath, line, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }
        catch
        {
        }
    }

    private static void RotateIfNeeded()
    {
        try
        {
            if (!File.Exists(LogPath))
            {
                return;
            }

            var info = new FileInfo(LogPath);
            if (info.Length < MaxLogSizeBytes)
            {
                return;
            }

            if (File.Exists(BackupLogPath))
            {
                File.Delete(BackupLogPath);
            }

            File.Move(LogPath, BackupLogPath);
        }
        catch
        {
        }
    }
}
