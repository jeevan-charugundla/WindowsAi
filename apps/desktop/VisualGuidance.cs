using System.Text.Json.Serialization;

namespace WindowsAICompanion.Desktop;

/// <summary>
/// V1.5.3 — Structured Guide Me target returned by POST /api/ai/visual-teacher.
/// This is NOT a chat message: it is a detection result consumed by the
/// visual teaching overlay. When the requested element is not visible, the
/// backend returns Target = null and a short no-target instruction.
/// </summary>
internal sealed record VisualGuidance(
    string Instruction,
    VisualTarget? Target,
    VisualTarget? NormalizedTarget,
    string? Label,
    double Confidence);

/// <summary>Normalized 0–1000 grid rectangle or backend pixel rectangle.</summary>
internal sealed record VisualTarget(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);
