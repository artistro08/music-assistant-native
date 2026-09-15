using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace MusicAssistant;

/// <summary>Render a XAML element to a PNG file. Used for the sidebar avatar and for diagnostics snapshots.</summary>
public static class Png
{
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? snapshotTimer;

    /// <summary>Renders an element offscreen and writes it to a PNG file, creating the folder if needed.</summary>
    /// <param name="element">The XAML element to render.</param>
    /// <param name="path">Full path of the PNG file to write.</param>
    /// <param name="width">Render width in pixels, or 0 to use the element's own size.</param>
    /// <param name="height">Render height in pixels, or 0 to use the element's own size.</param>
    /// <returns><see langword="true"/> when the file was written; <see langword="false"/> when the render produced no pixels.</returns>
    public static async Task<bool> SaveAsync(UIElement element, string path, int width = 0, int height = 0)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element, width, height);
        if (bitmap.PixelWidth == 0) return false;

        byte[] pixels = (await bitmap.GetPixelsAsync()).ToArray();
        using var stream = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();

        byte[] bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes);
        return true;
    }

    /// <summary>
    /// Diagnostics: when MA_SNAPSHOT names a file, save a render of the window
    /// to it a few seconds after launch. Lets the UI be checked on machines
    /// where screen capture is unavailable. Off unless the variable is set.
    /// </summary>
    /// <param name="root">The window's root element to render.</param>
    public static void ScheduleSnapshot(UIElement root)
    {
        string? target = Environment.GetEnvironmentVariable("MA_SNAPSHOT");
        if (string.IsNullOrEmpty(target)) return;

        // Held in a field so the GC cannot collect it before it fires.
        snapshotTimer = root.DispatcherQueue.CreateTimer();
        snapshotTimer.Interval    = TimeSpan.FromSeconds(8);
        snapshotTimer.IsRepeating = false;
        snapshotTimer.Tick += async (_, _) =>
        {
            try
            {
                if (!await SaveAsync(root, target)) App.Log("Snapshot rendered no pixels");
            }
            catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))
            {
                App.Log($"Snapshot failed: {ex}");
            }
        };
        snapshotTimer.Start();
    }
}
