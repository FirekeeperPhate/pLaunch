namespace pLaunch.Services;

/// <summary>Appends unexpected errors to errors.log in the data folder (kept small).</summary>
public static class ErrorLog
{
    const long MaxBytes = 256 * 1024;

    public static string FilePath { get; } = Path.Combine(AppConfig.ConfigDirectory, "errors.log");

    public static void Write(string context, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.ConfigDirectory);
            var info = new FileInfo(FilePath);
            if (info.Exists && info.Length > MaxBytes)
                info.Delete();
            File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception logEx) when (logEx is IOException or UnauthorizedAccessException)
        {
            // Logging must never become a second failure
        }
    }
}
