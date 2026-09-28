using System.Globalization;
using System.Text;
using Avalonia.Threading;

namespace HighlightCut.App.Services;

/// <summary>
/// Writes unexpected exceptions to <c>%LOCALAPPDATA%\HighlightCut\logs\highlightcut-yyyyMMdd.log</c>. An exception on
/// the UI thread is logged and reported in the status bar instead of closing the editor (a saved
/// project is also autosaved); anything else is logged before the process ends. A few notes go there too,
/// such as moving the app's folder from its old name.
/// </summary>
public static class CrashLog
{
    private static readonly Lock Gate = new();

    public static string Folder { get; set; } = AppDataFolder.PathFor("logs");

    /// <summary>The log file written today.</summary>
    public static string CurrentFile => Path.Combine(Folder, $"highlightcut-{DateTime.Now:yyyyMMdd}.log");

    /// <summary>Hooks the process, task and UI-thread handlers.</summary>
    /// <param name="report">Shows a short message to the user (UI thread).</param>
    public static void Install(Action<string> report)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("Unhandled exception (the app is closing)", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Write("Unhandled exception on the UI thread", e.Exception);
            e.Handled = true;
            report($"Something went wrong: {e.Exception.Message} (details in {CurrentFile})");
        };
    }

    /// <summary>Appends an entry; never throws.</summary>
    public static void Write(string title, Exception? exception) => Append(title, exception?.ToString() ?? "(no exception)");

    /// <summary>Appends an entry about something that went right (moving the old app folder, say); never throws.</summary>
    public static void Note(string title, string details) => Append(title, details);

    private static void Append(string title, string details)
    {
        try
        {
            var text = new StringBuilder()
                .Append(CultureInfo.InvariantCulture, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {title}")
                .AppendLine()
                .Append(CultureInfo.InvariantCulture, $"HighlightCut {typeof(CrashLog).Assembly.GetName().Version} · {Environment.OSVersion}")
                .AppendLine()
                .AppendLine(details)
                .AppendLine();
            lock (Gate)
            {
                Directory.CreateDirectory(Folder);
                File.AppendAllText(CurrentFile, text.ToString());
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nowhere to write; nothing more to do.
        }
    }
}
