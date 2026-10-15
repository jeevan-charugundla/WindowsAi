using System.IO;
using System.Text.RegularExpressions;

namespace WindowsAICompanion.Desktop.Tests;

/// <summary>
/// Source-level guard tests (STEP 10 of the V1.5.3 spec): the Guide Me flow
/// must use the dedicated visual-teacher endpoint and response model, and must
/// never route through /api/ai/chat or ChatMessage.
/// </summary>
public class SourceWiringTests
{
    private static string ReadRepoFile(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "WindowsAICompanion.sln")))
            {
                return File.ReadAllText(Path.Combine(dir, relativePath));
            }
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }

    [Fact]
    public void GuideMeButton_IsWiredToVisualTeacherHandler()
    {
        var xaml = ReadRepoFile(Path.Combine("apps", "desktop", "MainWindow.xaml"));
        Assert.Matches(
            new Regex(@"x:Name=""VisualTeacherButton""[\s\S]{0,200}Content=""Guide Me""[\s\S]{0,200}Click=""VisualTeacherButton_Click"""),
            xaml);
    }

    [Fact]
    public void GuideMeFlow_CallsVisualTeacherEndpoint()
    {
        var source = ReadRepoFile(Path.Combine("apps", "desktop", "MainWindow.xaml.cs"));
        Assert.Contains("VisualTeacherAsync", source);
    }

    [Fact]
    public void GuideMeFlow_NeverCallsChatEndpointOrChatModel()
    {
        // The Guide Me method (ShowVisualGuidanceAsync) must not deserialize into
        // ChatMessage or post to /api/ai/chat — chat stays untouched by design.
        var source = ReadRepoFile(Path.Combine("apps", "desktop", "MainWindow.xaml.cs"));
        var start = source.IndexOf("private async Task ShowVisualGuidanceAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0, "ShowVisualGuidanceAsync method not found.");
        var end = source.IndexOf("private void VisualOverlay_Closed", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not determine ShowVisualGuidanceAsync body bounds.");
        var body = source[start..end];

        Assert.DoesNotContain("ChatAsync", body);
        Assert.DoesNotContain("ChatMessage>", body);
        Assert.DoesNotContain("_conversation.Add", body);
    }

    [Fact]
    public void VisualTeacherClient_PostsToDedicatedEndpoint()
    {
        var source = ReadRepoFile(Path.Combine("apps", "desktop", "BackendApiClient.cs"));
        Assert.Contains("api/ai/visual-teacher", source);
        Assert.Contains("Task<VisualGuidanceResponse> VisualTeacherAsync", source);
        Assert.DoesNotContain("VisualGuidanceResponse> ReadFromJsonAsync<ChatMessage", source);
    }

    [Fact]
    public void VisualTeacherResponse_IsDedicatedModel_NotChatMessage()
    {
        var source = ReadRepoFile(Path.Combine("apps", "desktop", "BackendApiClient.cs"));
        var modelStart = source.IndexOf("record VisualGuidanceResponse", StringComparison.Ordinal);
        Assert.True(modelStart >= 0, "VisualGuidanceResponse model not found.");
        Assert.Contains("Instruction", source[modelStart..]);
        Assert.Contains("NormalizedTarget", source[modelStart..]);
        Assert.Contains("Label", source[modelStart..]);
        Assert.Contains("Confidence", source[modelStart..]);
    }

    [Fact]
    public void Overlay_NeverMovesTheRealMouse()
    {
        // V1.5.2 safety contract: no physical mouse control APIs anywhere in the
        // desktop app (STEP 13). The teaching cursor is purely visual.
        foreach (var file in new[]
                 {
                     Path.Combine("apps", "desktop", "VisualOverlayWindow.xaml.cs"),
                     Path.Combine("apps", "desktop", "VisualTargetMapper.cs"),
                     Path.Combine("apps", "desktop", "MainWindow.xaml.cs"),
                 })
        {
            var source = ReadRepoFile(file);
            Assert.DoesNotContain("SetCursorPos", source);
            Assert.DoesNotContain("Cursor.Position", source);
            Assert.DoesNotContain("SendInput", source);
            Assert.DoesNotContain("mouse_event", source);
        }
    }
}
