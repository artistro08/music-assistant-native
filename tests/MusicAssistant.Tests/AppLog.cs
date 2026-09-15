namespace MusicAssistant;

/// <summary>
/// Stands in for the app's logging entry points, which the linked sources call but which live in the WinUI
/// <c>App</c> class. Tests collect the lines instead of writing them to the log file.
/// </summary>
internal static class App
{
    /// <summary>Everything the code under test logged, newest last.</summary>
    public static List<string> Lines { get; } = [];

    /// <summary>Records a log line.</summary>
    /// <param name="message">The message.</param>
    public static void Log(string message)
    {
        lock (Lines) Lines.Add(message);
    }

    /// <summary>Records a verbose log line.</summary>
    /// <param name="message">The message.</param>
    public static void Debug(string message) => Log(message);
}
