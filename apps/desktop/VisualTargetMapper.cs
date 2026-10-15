namespace WindowsAICompanion.Desktop;

/// <summary>
/// V1.5.3 — Pure coordinate math for the visual teaching overlay.
///
/// Converts a normalized 0–1000 grid target into overlay-canvas DIPs:
/// 1. normalized grid → physical pixels on the captured monitor
/// 2. physical pixels → overlay canvas DIPs (origin at the virtual screen's
///    top-left, divided by the overlay window's DPI scale).
///
/// Pure function: no window, no Win32, no state — fully unit-testable.
/// </summary>
internal static class VisualTargetMapper
{
    /// <summary>The normalized coordinate grid maximum (V1.5.1).</summary>
    public const double NormalizedMax = 1000.0;

    /// <summary>Minimum rendered highlight size so tiny targets stay visible.</summary>
    public const double MinHighlightWidthDips = 40.0;
    public const double MinHighlightHeightDips = 20.0;

    public static (double Left, double Top, double Width, double Height) ToOverlayDips(
        VisualTarget target,
        System.Drawing.Rectangle monitorBounds,
        double overlayDpi,
        double virtualScreenLeft,
        double virtualScreenTop)
    {
        var scale = Math.Max(1.0, overlayDpi / 96.0);

        // Step 1: normalized (0-1000) → physical pixels on the captured monitor.
        var physX = monitorBounds.Left + (target.X / NormalizedMax) * monitorBounds.Width;
        var physY = monitorBounds.Top + (target.Y / NormalizedMax) * monitorBounds.Height;
        var physW = (target.Width / NormalizedMax) * monitorBounds.Width;
        var physH = (target.Height / NormalizedMax) * monitorBounds.Height;

        // Step 2: physical pixels → overlay canvas DIPs. The overlay canvas
        // origin is at (virtualScreenLeft, virtualScreenTop) in DIPs, so an
        // absolute physical point lands at physX/scale - virtualScreenLeft.
        var overlayX = (physX / scale) - virtualScreenLeft;
        var overlayY = (physY / scale) - virtualScreenTop;
        var overlayW = Math.Max(MinHighlightWidthDips, physW / scale);
        var overlayH = Math.Max(MinHighlightHeightDips, physH / scale);

        return (overlayX, overlayY, overlayW, overlayH);
    }
}
