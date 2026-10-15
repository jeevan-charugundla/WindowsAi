using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WindowsAICompanion.Desktop;

/// <summary>
/// Transparent fullscreen overlay that shows the HeyClicky-style AI teaching cursor
/// animation, target highlight, and instruction bubble.
///
/// Safety: this window NEVER moves the real Windows cursor. It is a purely visual
/// overlay rendered by WPF. Click-through is applied via WS_EX_TRANSPARENT so the
/// user can continue using their real mouse normally while the overlay is visible.
/// </summary>
public partial class VisualOverlayWindow : Window
{
    // -------------------------------------------------------------------------
    // Win32 click-through
    // -------------------------------------------------------------------------
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hwnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;

    // -------------------------------------------------------------------------
    // Cursor canvas dimensions (matches XAML Width/Height on CursorCanvas)
    // -------------------------------------------------------------------------
    private const double CursorWidth = 32.0;
    private const double CursorHeight = 44.0;

    // -------------------------------------------------------------------------
    // Auto-dismiss timer
    // -------------------------------------------------------------------------
    private readonly DispatcherTimer _dismissTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private Storyboard? _mainStoryboard;

    // -------------------------------------------------------------------------
    // State
    // -------------------------------------------------------------------------
    private readonly VisualGuidance _guidance;
    private readonly System.Drawing.Rectangle _monitorBounds;  // physical pixel bounds of the captured monitor

    internal VisualOverlayWindow(VisualGuidance guidance, System.Drawing.Rectangle monitorBounds)
    {
        _guidance = guidance;
        _monitorBounds = monitorBounds;
        InitializeComponent();
        _dismissTimer.Tick += (_, _) => Close();
    }

    // -------------------------------------------------------------------------
    // Win32 — make the overlay click-through so user mouse input falls through
    // -------------------------------------------------------------------------
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var current = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, current | WS_EX_TRANSPARENT | WS_EX_LAYERED);

        // Hook Escape via ComponentDispatcher so it works even though ShowActivated=False
        ComponentDispatcher.ThreadPreprocessMessage += OnThreadPreprocessMessage;
    }

    private void OnThreadPreprocessMessage(ref MSG msg, ref bool handled)
    {
        const int WM_KEYDOWN = 0x0100;
        const int VK_ESCAPE = 0x1B;
        if (!handled && msg.message == WM_KEYDOWN && (int)msg.wParam == VK_ESCAPE)
        {
            handled = true;
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        ComponentDispatcher.ThreadPreprocessMessage -= OnThreadPreprocessMessage;
        _mainStoryboard?.Stop(this);
        _dismissTimer.Stop();
        base.OnClosed(e);
    }

    // -------------------------------------------------------------------------
    // Loaded — position window and start animation
    // -------------------------------------------------------------------------
    private void OverlayWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Cover the full virtual screen so the overlay spans all monitors.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        // Position dismiss button in the top-right corner of the virtual screen.
        Canvas.SetRight(DismissButton, 12);
        // Canvas.SetRight doesn't work directly — use Left with Width offset.
        Canvas.SetLeft(DismissButton, SystemParameters.VirtualScreenWidth - 44);
        Canvas.SetTop(DismissButton, 12);
        DismissButton.Visibility = Visibility.Visible;

        // Show guidance only when a valid target is available. In message-only
        // mode (no target) no animation runs and no highlight is drawn.
        var normalized = _guidance.NormalizedTarget ?? _guidance.Target;
        if (normalized is not null)
        {
            StartGuidanceAnimation(normalized);
        }
    }

    // -------------------------------------------------------------------------
    // Coordinate conversion: normalized AI coords (0–1000) → overlay canvas DIPs
    // -------------------------------------------------------------------------
    private (double left, double top, double width, double height) ConvertToOverlayDips(VisualTarget target)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var dpi = GetDpiForWindow(hwnd);

        // Pure math lives in VisualTargetMapper (unit-tested); this only supplies
        // window-bound values: the overlay's DPI and the virtual screen origin.
        return VisualTargetMapper.ToOverlayDips(
            target,
            _monitorBounds,
            dpi,
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop);
    }

    // -------------------------------------------------------------------------
    // Animation — teaching cursor + highlight + bubble
    // -------------------------------------------------------------------------
    private void StartGuidanceAnimation(VisualTarget target)
    {
        var (targetLeft, targetTop, targetWidth, targetHeight) = ConvertToOverlayDips(target);

        // Cursor destination: hover just above the target center-top.
        var cursorDestX = targetLeft + targetWidth / 2.0 - CursorWidth / 2.0;
        var cursorDestY = targetTop - CursorHeight - 4.0;

        // Cursor start: offset to the left and upward so it visually travels toward the target.
        var cursorStartX = cursorDestX - 150.0;
        var cursorStartY = cursorDestY - 80.0;

        // Place cursor at start position.
        Canvas.SetLeft(CursorCanvas, cursorStartX);
        Canvas.SetTop(CursorCanvas, cursorStartY);
        CursorCanvas.Visibility = Visibility.Visible;

        // Build a single storyboard with phased timings.
        _mainStoryboard = new Storyboard();

        // ── Phase 1 (0–700 ms): Cursor travels to destination ──────────────
        var easeCubic = new CubicEase { EasingMode = EasingMode.EaseOut };

        var cursorMoveX = new DoubleAnimation
        {
            From = cursorStartX,
            To = cursorDestX,
            Duration = new Duration(TimeSpan.FromMilliseconds(700)),
            EasingFunction = easeCubic,
            FillBehavior = FillBehavior.HoldEnd
        };
        Storyboard.SetTarget(cursorMoveX, CursorCanvas);
        Storyboard.SetTargetProperty(cursorMoveX, new PropertyPath("(Canvas.Left)"));

        var cursorMoveY = new DoubleAnimation
        {
            From = cursorStartY,
            To = cursorDestY,
            Duration = new Duration(TimeSpan.FromMilliseconds(700)),
            EasingFunction = easeCubic,
            FillBehavior = FillBehavior.HoldEnd
        };
        Storyboard.SetTarget(cursorMoveY, CursorCanvas);
        Storyboard.SetTargetProperty(cursorMoveY, new PropertyPath("(Canvas.Top)"));

        // ── Phase 2 (700–900 ms): Highlight and pulse ring fade in ─────────
        HighlightBorder.Visibility = Visibility.Visible;
        Canvas.SetLeft(HighlightBorder, targetLeft);
        Canvas.SetTop(HighlightBorder, targetTop);
        HighlightBorder.Width = targetWidth;
        HighlightBorder.Height = targetHeight;

        var highlightFadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            BeginTime = TimeSpan.FromMilliseconds(700),
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            FillBehavior = FillBehavior.HoldEnd
        };
        Storyboard.SetTarget(highlightFadeIn, HighlightBorder);
        Storyboard.SetTargetProperty(highlightFadeIn, new PropertyPath(OpacityProperty));

        var pulseRingFadeIn = new DoubleAnimation
        {
            From = 0,
            To = 0.75,
            BeginTime = TimeSpan.FromMilliseconds(700),
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            FillBehavior = FillBehavior.HoldEnd
        };
        Storyboard.SetTarget(pulseRingFadeIn, PulseRing);
        Storyboard.SetTargetProperty(pulseRingFadeIn, new PropertyPath(OpacityProperty));

        // ── Phase 3 (900 ms onward): Highlight pulses gently ───────────────
        var highlightPulse = new DoubleAnimation
        {
            From = 1.0,
            To = 0.55,
            BeginTime = TimeSpan.FromMilliseconds(900),
            Duration = new Duration(TimeSpan.FromMilliseconds(900)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            FillBehavior = FillBehavior.HoldEnd
        };
        Storyboard.SetTarget(highlightPulse, HighlightBorder);
        Storyboard.SetTargetProperty(highlightPulse, new PropertyPath(OpacityProperty));

        var pulseRingScale = new DoubleAnimation
        {
            From = 1.0,
            To = 1.4,
            BeginTime = TimeSpan.FromMilliseconds(900),
            Duration = new Duration(TimeSpan.FromMilliseconds(600)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        Storyboard.SetTarget(pulseRingScale, PulseRingScale);
        Storyboard.SetTargetProperty(pulseRingScale, new PropertyPath(ScaleTransform.ScaleXProperty));

        var pulseRingScaleY = new DoubleAnimation
        {
            From = 1.0,
            To = 1.4,
            BeginTime = TimeSpan.FromMilliseconds(900),
            Duration = new Duration(TimeSpan.FromMilliseconds(600)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        Storyboard.SetTarget(pulseRingScaleY, PulseRingScale);
        Storyboard.SetTargetProperty(pulseRingScaleY, new PropertyPath(ScaleTransform.ScaleYProperty));

        // ── Phase 4 (800–1100 ms): Instruction bubble fades in ─────────────
        PositionInstructionBubble(targetLeft, targetTop, targetWidth, targetHeight);
        InstructionTextBlock.Text = _guidance.Instruction;
        InstructionBubble.Visibility = Visibility.Visible;

        var bubbleFadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            BeginTime = TimeSpan.FromMilliseconds(800),
            Duration = new Duration(TimeSpan.FromMilliseconds(300)),
            FillBehavior = FillBehavior.HoldEnd
        };
        Storyboard.SetTarget(bubbleFadeIn, InstructionBubble);
        Storyboard.SetTargetProperty(bubbleFadeIn, new PropertyPath(OpacityProperty));

        // Add all animations to storyboard.
        _mainStoryboard.Children.Add(cursorMoveX);
        _mainStoryboard.Children.Add(cursorMoveY);
        _mainStoryboard.Children.Add(highlightFadeIn);
        _mainStoryboard.Children.Add(pulseRingFadeIn);
        _mainStoryboard.Children.Add(highlightPulse);
        _mainStoryboard.Children.Add(pulseRingScale);
        _mainStoryboard.Children.Add(pulseRingScaleY);
        _mainStoryboard.Children.Add(bubbleFadeIn);

        _mainStoryboard.Begin(this, isControllable: true);

        // Auto-dismiss after 8 seconds total.
        _dismissTimer.Start();
    }

    // -------------------------------------------------------------------------
    // Instruction bubble positioning
    // -------------------------------------------------------------------------
    private void PositionInstructionBubble(double targetLeft, double targetTop, double targetWidth, double targetHeight)
    {
        const double bubbleWidth = 420.0;
        const double estimatedBubbleHeight = 60.0;
        const double margin = 12.0;
        const double edgePad = 16.0;

        InstructionBubble.Width = Math.Min(bubbleWidth, Width - edgePad * 2);

        // Prefer above target.
        double bubbleTop;
        if (targetTop - estimatedBubbleHeight - margin >= edgePad)
        {
            bubbleTop = targetTop - estimatedBubbleHeight - margin;
        }
        else if (targetTop + targetHeight + margin + estimatedBubbleHeight <= Height - edgePad)
        {
            bubbleTop = targetTop + targetHeight + margin;
        }
        else
        {
            // Place at safe vertical position.
            bubbleTop = Math.Clamp(targetTop, edgePad, Math.Max(edgePad, Height - estimatedBubbleHeight - edgePad));
        }

        // Center horizontally over target, clamped to screen.
        var bubbleLeft = targetLeft + targetWidth / 2.0 - InstructionBubble.Width / 2.0;
        bubbleLeft = Math.Clamp(bubbleLeft, edgePad, Math.Max(edgePad, Width - InstructionBubble.Width - edgePad));

        Canvas.SetLeft(InstructionBubble, bubbleLeft);
        Canvas.SetTop(InstructionBubble, bubbleTop);
    }

    // -------------------------------------------------------------------------
    // Dismiss
    // -------------------------------------------------------------------------
    private void DismissButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
