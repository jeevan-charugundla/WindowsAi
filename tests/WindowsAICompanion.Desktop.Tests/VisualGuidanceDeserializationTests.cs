using System.Text.Json;
using WindowsAICompanion.Desktop;

namespace WindowsAICompanion.Desktop.Tests;

/// <summary>
/// STEP 8/9 of the V1.5.3 spec: the desktop must deserialize the visual-teacher
/// response into the dedicated VisualGuidanceResponse model (never ChatMessage),
/// and the overlay's normalized→screen conversion must be correct.
/// </summary>
public class VisualGuidanceDeserializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Deserializes_ValidTargetResponse()
    {
        const string json = """
            {
              "success": true,
              "instruction": "Click the File menu.",
              "target": { "x": 210, "y": 42, "width": 55, "height": 30 },
              "normalizedTarget": { "x": 35, "y": 25, "width": 55, "height": 30 },
              "label": "File menu",
              "confidence": 0.97
            }
            """;

        var result = JsonSerializer.Deserialize<VisualGuidanceResponse>(json, Options);

        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal("Click the File menu.", result.Instruction);
        Assert.NotNull(result.Target);
        Assert.Equal(210, result.Target!.X);
        Assert.Equal(42, result.Target.Y);
        Assert.Equal(55, result.Target.Width);
        Assert.Equal(30, result.Target.Height);
        Assert.NotNull(result.NormalizedTarget);
        Assert.Equal(35, result.NormalizedTarget!.X);
        Assert.Equal("File menu", result.Label);
        Assert.Equal(0.97, result.Confidence, precision: 3);
    }

    [Fact]
    public void Deserializes_NoTargetResponse()
    {
        const string json = """
            {
              "success": true,
              "instruction": "I cannot find the requested element on the current screen.",
              "target": null,
              "label": null,
              "confidence": 0.0
            }
            """;

        var result = JsonSerializer.Deserialize<VisualGuidanceResponse>(json, Options);

        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal("I cannot find the requested element on the current screen.", result.Instruction);
        Assert.Null(result.Target);
        Assert.Null(result.NormalizedTarget);
        Assert.Null(result.Label);
        Assert.Equal(0.0, result.Confidence, precision: 3);
    }

    [Fact]
    public void Deserializes_ErrorResponse()
    {
        const string json = """{ "success": false, "error": "A visual question is required." }""";

        var result = JsonSerializer.Deserialize<VisualGuidanceResponse>(json, Options);

        Assert.NotNull(result);
        Assert.False(result!.Success);
        Assert.Equal("A visual question is required.", result.Error);
    }

    [Fact]
    public void Confidence_DefaultsToZero_WhenMissing()
    {
        const string json = """{ "success": true, "instruction": "Click it.", "target": null }""";

        var result = JsonSerializer.Deserialize<VisualGuidanceResponse>(json, Options);

        Assert.NotNull(result);
        Assert.Equal(0.0, result!.Confidence, precision: 3);
    }
}

/// <summary>
/// STEP 9 of the V1.5.3 spec: normalized (0–1000) grid coordinates convert to
/// correct overlay-canvas DIPs for both 100% and 150% DPI scales.
/// </summary>
public class VisualTargetMapperTests
{
    // A 1920x1080 monitor at origin (0,0).
    private static readonly System.Drawing.Rectangle Monitor = new(0, 0, 1920, 1080);

    [Fact]
    public void NormalizedTarget_At96Dpi_MapsLinearly()
    {
        // Normalized center of the screen: (500, 500) → physical center (960, 540).
        var target = new VisualTarget(447, 462, 60, 55); // ~46% across, ~50% down

        var (left, top, width, height) = VisualTargetMapper.ToOverlayDips(
            target, Monitor, overlayDpi: 96.0, virtualScreenLeft: 0, virtualScreenTop: 0);

        Assert.Equal(0.447 * 1920, left, precision: 1);
        Assert.Equal(0.462 * 1080, top, precision: 1);
        Assert.Equal(0.060 * 1920, width, precision: 1);
        Assert.Equal(0.055 * 1080, height, precision: 1);
    }

    [Fact]
    public void NormalizedTarget_At144Dpi_DividesByScale()
    {
        // At 150% scale, physical 960 maps to 960/1.5 = 640 DIPs.
        var target = new VisualTarget(500, 500, 100, 100);

        var (left, top, width, height) = VisualTargetMapper.ToOverlayDips(
            target, Monitor, overlayDpi: 144.0, virtualScreenLeft: 0, virtualScreenTop: 0);

        Assert.Equal(960 / 1.5, left, precision: 1);
        Assert.Equal(540 / 1.5, top, precision: 1);
        Assert.Equal((100 / 1000.0 * 1920) / 1.5, width, precision: 1);
        Assert.Equal((100 / 1000.0 * 1080) / 1.5, height, precision: 1);
    }

    [Fact]
    public void SecondaryMonitor_OffsetIsRespected()
    {
        // Monitor to the right of the primary at physical x=1920, 2560x1440.
        var monitor = new System.Drawing.Rectangle(1920, 0, 2560, 1440);
        var target = new VisualTarget(500, 500, 100, 100);

        var (left, top, width, height) = VisualTargetMapper.ToOverlayDips(
            target, monitor, overlayDpi: 96.0, virtualScreenLeft: 0, virtualScreenTop: 0);

        // Normalized 500 → physical 1920 + 1280 = 3200.
        Assert.Equal(3200.0, left, precision: 1);
        Assert.Equal(720.0, top, precision: 1);
    }

    [Fact]
    public void TinyTargets_AreClampedToMinimumVisibleSize()
    {
        var target = new VisualTarget(500, 500, 1, 1); // 1.92x1.08 physical px

        var (_, _, width, height) = VisualTargetMapper.ToOverlayDips(
            target, Monitor, overlayDpi: 96.0, virtualScreenLeft: 0, virtualScreenTop: 0);

        Assert.Equal(VisualTargetMapper.MinHighlightWidthDips, width);
        Assert.Equal(VisualTargetMapper.MinHighlightHeightDips, height);
    }

    [Fact]
    public void GridCorners_MapToScreenCorners()
    {
        var topLeft = new VisualTarget(0, 0, 10, 10);
        var (left, top, _, _) = VisualTargetMapper.ToOverlayDips(
            topLeft, Monitor, overlayDpi: 96.0, virtualScreenLeft: 0, virtualScreenTop: 0);
        Assert.Equal(0.0, left, precision: 1);
        Assert.Equal(0.0, top, precision: 1);

        var bottomRight = new VisualTarget(990, 990, 10, 10);
        var (left2, top2, _, _) = VisualTargetMapper.ToOverlayDips(
            bottomRight, Monitor, overlayDpi: 96.0, virtualScreenLeft: 0, virtualScreenTop: 0);
        Assert.Equal(0.990 * 1920, left2, precision: 1);
        Assert.Equal(0.990 * 1080, top2, precision: 1);
    }
}
