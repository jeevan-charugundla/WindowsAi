using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WindowsAICompanion.Desktop;

/// <summary>
/// Typed HTTP client for the Windows AI Companion backend.
///
/// V1.5.3 — Guide Me has a DEDICATED response model
/// (<see cref="VisualGuidanceResponse"/>) deserialized from
/// POST /api/ai/visual-teacher. It is never deserialized into ChatMessage and
/// never rendered through the normal Markdown chat pipeline.
/// </summary>
internal sealed class BackendApiClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Uri _analyzeScreenUri;
    private readonly Uri _chatUri;
    private readonly Uri _visualTeacherUri;
    private readonly Uri _transcribeUri;

    public BackendApiClient(string baseUrl = "http://localhost:3000")
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromSeconds(60)
        };
        _analyzeScreenUri = new Uri("api/ai/analyze-screen", UriKind.Relative);
        _chatUri = new Uri("api/ai/chat", UriKind.Relative);
        _visualTeacherUri = new Uri("api/ai/visual-teacher", UriKind.Relative);
        _transcribeUri = new Uri("api/ai/transcribe", UriKind.Relative);
    }

    public async Task<string> AnalyzeScreenAsync(string question, string imageDataUrl)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                _analyzeScreenUri,
                new AnalyzeScreenRequest(question, imageDataUrl));
            var result = await response.Content.ReadFromJsonAsync<AnalyzeScreenResponse>();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(result?.Error ?? "The AI backend returned an error.");
            }

            if (result?.Success != true || string.IsNullOrWhiteSpace(result.Answer))
            {
                throw new InvalidOperationException("The AI backend returned an empty response.");
            }

            return result.Answer;
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException("The AI request timed out. Please try again.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Unable to connect to the AI backend.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The AI backend returned an invalid response.");
        }
    }

    public async Task<string> ChatAsync(IReadOnlyList<ChatMessage> messages)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                _chatUri,
                new ChatRequest(messages));
            var result = await response.Content.ReadFromJsonAsync<ChatResponse>();

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(result?.Error ?? "The AI backend returned an error.");
            }

            if (result?.Success != true || string.IsNullOrWhiteSpace(result.Answer))
            {
                throw new InvalidOperationException("The AI backend returned an empty response.");
            }

            return result.Answer;
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException("The request timed out. Please try again.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Unable to connect to the AI backend.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The AI backend returned an invalid response.");
        }
    }

    /// <summary>
    /// V1.5.3 — Sends the question, screenshot, and screenshot dimensions to
    /// the dedicated visual-teacher endpoint and returns the structured
    /// <see cref="VisualGuidanceResponse"/>. Never touches the /api/ai/chat
    /// endpoint and never returns raw AI prose.
    /// </summary>
    public async Task<VisualGuidanceResponse> VisualTeacherAsync(string question, CapturedScreen screen)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                _visualTeacherUri,
                new VisualTeacherRequest(question, screen.DataUrl, screen.Width, screen.Height));
            var result = await response.Content.ReadFromJsonAsync<VisualGuidanceResponse>();

            if (result is null)
            {
                throw new InvalidOperationException("The AI backend returned an invalid visual target.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(result.Error ?? "The AI backend returned an error.");
            }

            if (result.Success != true || string.IsNullOrWhiteSpace(result.Instruction))
            {
                throw new InvalidOperationException("The AI backend returned an invalid visual target.");
            }

            return result;
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException("The request timed out. Please try again.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Unable to connect to the AI backend.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The AI backend returned an invalid response.");
        }
    }

    /// <summary>
    /// Sends WAV audio bytes to the backend transcription endpoint and returns the transcript text.
    /// Throws <see cref="InvalidOperationException"/> with a user-friendly message on any failure.
    /// The audio bytes are transmitted only to the configured backend; the API key never leaves the server.
    /// </summary>
    /// <param name="audioBytes">Raw WAV audio data.</param>
    /// <param name="language">Optional ISO 639-1 language code (e.g. "en"). Pass null for auto-detect.</param>
    public async Task<string> TranscribeAudioAsync(byte[] audioBytes, string? language = null)
    {
        try
        {
            using var content = new MultipartFormDataContent();

            // Attach audio as a file field named "audio"
            var audioContent = new ByteArrayContent(audioBytes);
            audioContent.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
            content.Add(audioContent, "audio", "recording.wav");

            // Optionally include the language hint
            if (!string.IsNullOrWhiteSpace(language))
            {
                content.Add(new StringContent(language.Trim()), "language");
            }

            using var response = await _httpClient.PostAsync(_transcribeUri, content);
            var result = await response.Content.ReadFromJsonAsync<TranscribeResponse>();

            if (!response.IsSuccessStatusCode || result?.Success != true)
            {
                var friendlyMessage = result?.Message
                    ?? "Voice transcription failed. Please try again.";
                throw new InvalidOperationException(friendlyMessage);
            }

            var text = result.Text?.Trim() ?? string.Empty;
            return text;
        }
        catch (InvalidOperationException)
        {
            throw; // Re-throw our own user-friendly messages
        }
        catch (TaskCanceledException)
        {
            throw new InvalidOperationException("The transcription request timed out. Please try again.");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Unable to connect to the AI backend for transcription.");
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The transcription service returned an invalid response.");
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    private sealed record AnalyzeScreenRequest(string Question, string Image);

    private sealed record AnalyzeScreenResponse(bool Success, string? Answer, string? Error);

    private sealed record ChatRequest(IReadOnlyList<ChatMessage> Messages);

    private sealed record ChatResponse(bool Success, string? Answer, string? Error);

    private sealed record VisualTeacherRequest(string Question, string Image, int ScreenWidth, int ScreenHeight);

    private sealed record TranscribeResponse(
        bool Success,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("message")] string? Message);
}

/// <summary>
/// V1.5.3 — Dedicated Guide Me response model (NOT ChatMessage).
/// Shape returned by POST /api/ai/visual-teacher:
/// { success, instruction, target, normalizedTarget, label, confidence }.
/// Target/NormalizedTarget/Label are null when the element was not found.
/// </summary>
internal sealed record VisualGuidanceResponse(
    bool? Success,
    [property: JsonPropertyName("instruction")] string? Instruction,
    [property: JsonPropertyName("target")] VisualTarget? Target,
    [property: JsonPropertyName("normalizedTarget")] VisualTarget? NormalizedTarget,
    [property: JsonPropertyName("label")] string? Label,
    [property: JsonPropertyName("confidence")] double Confidence,
    string? Error = null);
