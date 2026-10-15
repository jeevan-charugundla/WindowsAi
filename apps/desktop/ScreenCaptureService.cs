using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace WindowsAICompanion.Desktop;

internal sealed class ScreenCaptureService
{
    public string CapturePrimaryScreenAsDataUrl()
    {
        return CapturePrimaryScreen().DataUrl;
    }

    public CapturedScreen CapturePrimaryScreen()
    {
        var screen = Screen.PrimaryScreen ?? throw new InvalidOperationException("No primary display is available.");
        return CaptureScreen(screen);
    }

    /// <summary>
    /// Captures the monitor that contains the given WPF window.
    /// Returns the captured screenshot together with the monitor's physical pixel origin
    /// so the overlay can convert normalized AI coordinates back to screen coordinates.
    /// </summary>
    public CapturedScreen CaptureScreenContaining(System.Windows.Window wpfWindow)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(wpfWindow).EnsureHandle();
        var screen = Screen.FromHandle(hwnd);
        return CaptureScreen(screen);
    }

    private static CapturedScreen CaptureScreen(Screen screen)
    {
        var bounds = screen.Bounds;

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        }

        using var imageStream = new MemoryStream();
        bitmap.Save(imageStream, ImageFormat.Png);
        return new CapturedScreen(
            $"data:image/png;base64,{Convert.ToBase64String(imageStream.ToArray())}",
            bitmap.Width,
            bitmap.Height,
            bounds.Left,
            bounds.Top);
    }
}

/// <param name="DataUrl">PNG image encoded as a data URL.</param>
/// <param name="Width">Screenshot width in physical pixels.</param>
/// <param name="Height">Screenshot height in physical pixels.</param>
/// <param name="MonitorLeft">Physical pixel X origin of the monitor (may be negative on multi-monitor setups).</param>
/// <param name="MonitorTop">Physical pixel Y origin of the monitor (may be negative on multi-monitor setups).</param>
internal sealed record CapturedScreen(
    string DataUrl,
    int Width,
    int Height,
    int MonitorLeft = 0,
    int MonitorTop = 0);
