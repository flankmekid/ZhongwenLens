using System.Text;

namespace ZhongwenLens.App.Services;

/// <summary>
/// Appends diagnostics to <c>%LOCALAPPDATA%\ZhongwenLens\log.txt</c>.
/// </summary>
/// <remarks>
/// A tray-resident app with no console and no main window has nowhere to report a failed snip.
/// Without this, a snip that throws is completely silent — the overlay closes and nothing
/// happens, which is indistinguishable from the hotkey not firing.
/// </remarks>
public static class Log
{
    private static readonly Lock Gate = new();
    private static readonly string Path = System.IO.Path.Combine(DataPaths.UserDirectory, "log.txt");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(DataPaths.UserDirectory);

                // Truncate rather than rotate: this is a diagnostic aid, not an audit trail,
                // and an unbounded file on a long-running tray app is a slow leak.
                if (File.Exists(Path) && new FileInfo(Path).Length > 512 * 1024)
                {
                    File.Delete(Path);
                }

                File.AppendAllText(
                    Path,
                    $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (IOException)
        {
            // Diagnostics must never take down the thing they're diagnosing.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static void Error(string context, Exception exception)
        => Write($"ERROR {context}: {exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}");
}
