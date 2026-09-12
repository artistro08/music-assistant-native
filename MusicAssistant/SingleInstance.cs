using System.Runtime.InteropServices;

namespace MusicAssistant;

/// <summary>
/// Keeps one copy of the app running per user session.
///
/// A second launch hands over to the first one (its window comes to the
/// front, out of the tray if needed) and exits. Two copies would each act as
/// this PC's speaker with the same client id and fight over the connection.
/// The mutex lives in the session namespace, so a packaged (MSIX) copy and an
/// unpackaged copy see each other too.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\DevinGreen.MusicAssistant.Instance";

    /// <summary>Broadcast by a second launch; the running copy's window subclass (TrayIcon) answers by showing itself.</summary>
    public static readonly uint ActivateMessage = RegisterWindowMessage("DevinGreen.MusicAssistant.Activate");

    private static Mutex? mutex;   // held for the life of the process

    /// <summary>True when this process is the first; false after asking the running copy to show itself.</summary>
    public static bool Claim()
    {
        mutex = new Mutex(true, MutexName, out var first);
        if (first) return true;

        PostMessage(HWND_BROADCAST, ActivateMessage, IntPtr.Zero, IntPtr.Zero);
        return false;
    }

    private static readonly IntPtr HWND_BROADCAST = 0xFFFF;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
