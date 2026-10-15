using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace WindowsAICompanion.Desktop;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private bool _allowClose;
    private readonly BackendApiClient _backendApiClient;
    private readonly ScreenCaptureService _screenCaptureService;
    private readonly ObservableCollection<ChatMessage> _conversation = new();
    private VisualOverlayWindow? _visualOverlay;
    private bool _requestInProgress;

    // V1.6 — Voice input
    private MicrophoneRecorder? _micRecorder;
    private bool _isRecording;

    internal MainWindow(BackendApiClient backendApiClient, ScreenCaptureService screenCaptureService)
    {
        InitializeComponent();
        _backendApiClient = backendApiClient;
        _screenCaptureService = screenCaptureService;
        ConversationItemsControl.ItemsSource = _conversation;
        Closing += OnClosing;
    }

    public void AllowClose()
    {
        _allowClose = true;
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _visualOverlay?.Close();

        // Clean up recorder if window is closed while recording
        if (_isRecording)
        {
            _ = StopRecordingAndDiscardAsync();
        }
        _micRecorder?.Dispose();

        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        await SendMessageAsync(false);
    }

    private async void AnalyzeScreenButton_Click(object sender, RoutedEventArgs e)
    {
        await SendMessageAsync(true);
    }

    private async void VisualTeacherButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowVisualGuidanceAsync();
    }

    private void ClearChatButton_Click(object sender, RoutedEventArgs e)
    {
        if (_requestInProgress || _isRecording)
        {
            return;
        }

        _conversation.Clear();
        StatusTextBlock.Text = "Ready";
        QuestionTextBox.Clear();
        QuestionTextBox.Focus();
    }

    private async void QuestionTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await SendMessageAsync(false);
        }
    }

    // -------------------------------------------------------------------------
    // V1.6 — Microphone button
    // -------------------------------------------------------------------------

    private async void MicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRecording)
        {
            await StopRecordingAndTranscribeAsync();
        }
        else
        {
            await StartRecordingAsync();
        }
    }

    private async Task StartRecordingAsync()
    {
        if (_requestInProgress) return;

        // Create a fresh recorder each session
        _micRecorder?.Dispose();
        _micRecorder = new MicrophoneRecorder();

        // Wire up the 30-second auto-stop event (raised on a background thread)
        _micRecorder.MaxDurationReached += async (_, _) =>
        {
            // Marshal back to the UI thread
            await Dispatcher.InvokeAsync(async () =>
            {
                if (_isRecording)
                {
                    await StopRecordingAndTranscribeAsync();
                }
            });
        };

        var (success, errorMessage) = await _micRecorder.StartAsync();

        if (!success)
        {
            StatusTextBlock.Text = errorMessage ?? "Microphone unavailable.";
            _micRecorder.Dispose();
            _micRecorder = null;
            return;
        }

        _isRecording = true;
        SetMicButtonRecordingState(recording: true);
        SetControlsEnabled(enabled: false, exceptMic: true);
        StatusTextBlock.Text = "Listening...";
    }

    private async Task StopRecordingAndTranscribeAsync()
    {
        if (!_isRecording || _micRecorder is null) return;

        SetMicButtonRecordingState(recording: false);
        SetControlsEnabled(enabled: false, exceptMic: false);
        StatusTextBlock.Text = "Transcribing...";

        AudioRecordingResult result;
        try
        {
            result = await _micRecorder.StopAndGetResultAsync();
        }
        catch (Exception ex)
        {
            _isRecording = false;
            StatusTextBlock.Text = "Recording failed. Please try again.";
            _ = ex; // Not logged — no audio data in logs
            RestoreControlsAfterVoice();
            return;
        }
        finally
        {
            _micRecorder.Dispose();
            _micRecorder = null;
        }

        _isRecording = false;

        if (!result.Success)
        {
            var msg = result.ErrorMessage ?? "I couldn't hear anything. Please try again.";
            StatusTextBlock.Text = msg;
            RestoreControlsAfterVoice();
            return;
        }

        // Upload to backend for transcription
        string transcript;
        try
        {
            transcript = await _backendApiClient.TranscribeAudioAsync(result.AudioBytes!, language: "en");
        }
        catch (Exception ex)
        {
            StatusTextBlock.Text = ex.Message;
            RestoreControlsAfterVoice();
            return;
        }

        transcript = transcript.Trim();
        if (transcript.Length == 0)
        {
            StatusTextBlock.Text = "I couldn't hear anything. Please try again.";
            RestoreControlsAfterVoice();
            return;
        }

        // Insert transcript into text box — user decides what to do next
        QuestionTextBox.Text = transcript;
        QuestionTextBox.CaretIndex = transcript.Length;
        StatusTextBlock.Text = "Ready";
        RestoreControlsAfterVoice();
        QuestionTextBox.Focus();
    }

    private async Task StopRecordingAndDiscardAsync()
    {
        if (_micRecorder is null) return;
        try { await _micRecorder.StopAndGetResultAsync(); } catch { /* discard */ }
        _micRecorder.Dispose();
        _micRecorder = null;
        _isRecording = false;
    }

    private void SetMicButtonRecordingState(bool recording)
    {
        MicButton.Content = recording ? "🔴" : "🎙";
        MicButton.ToolTip = recording ? "Stop recording" : "Speak (click to start/stop recording)";
        System.Windows.Automation.AutomationProperties.SetName(
            MicButton,
            recording ? "Stop voice recording" : "Start voice input");
    }

    private void SetControlsEnabled(bool enabled, bool exceptMic)
    {
        SendButton.IsEnabled = enabled;
        AnalyzeScreenButton.IsEnabled = enabled;
        VisualTeacherButton.IsEnabled = enabled;
        ClearChatButton.IsEnabled = enabled;
        QuestionTextBox.IsEnabled = enabled;
        if (!exceptMic)
        {
            MicButton.IsEnabled = enabled;
        }
    }

    private void RestoreControlsAfterVoice()
    {
        SetControlsEnabled(enabled: true, exceptMic: false);
    }

    // -------------------------------------------------------------------------
    // Existing chat / screen / visual teacher logic (unchanged from V1.5.1)
    // -------------------------------------------------------------------------

    private async Task SendMessageAsync(bool analyzeScreen)
    {
        var question = QuestionTextBox.Text.Trim();
        if (question.Length == 0)
        {
            StatusTextBlock.Text = "Enter a question first.";
            QuestionTextBox.Focus();
            return;
        }

        AddMessage(new ChatMessage("user", question));
        _requestInProgress = true;
        SendButton.IsEnabled = false;
        AnalyzeScreenButton.IsEnabled = false;
        VisualTeacherButton.IsEnabled = false;
        ClearChatButton.IsEnabled = false;
        MicButton.IsEnabled = false;
        QuestionTextBox.IsEnabled = false;
        StatusTextBlock.Text = analyzeScreen ? "Analyzing screen..." : "Thinking...";
        QuestionTextBox.Clear();

        try
        {
            var answer = analyzeScreen
                ? await _backendApiClient.AnalyzeScreenAsync(question, _screenCaptureService.CapturePrimaryScreenAsDataUrl())
                : await _backendApiClient.ChatAsync(_conversation.ToList());

            AddMessage(new ChatMessage("assistant", answer));
            StatusTextBlock.Text = "Ready";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = exception.Message;
        }
        finally
        {
            _requestInProgress = false;
            SendButton.IsEnabled = true;
            AnalyzeScreenButton.IsEnabled = true;
            VisualTeacherButton.IsEnabled = true;
            ClearChatButton.IsEnabled = true;
            MicButton.IsEnabled = true;
            QuestionTextBox.IsEnabled = true;
            QuestionTextBox.Focus();
        }
    }

    private void AddMessage(ChatMessage message)
    {
        _conversation.Add(message);
        ChatScrollViewer.Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() => ChatScrollViewer.ScrollToEnd()));
    }

    private async Task ShowVisualGuidanceAsync()
    {
        var question = QuestionTextBox.Text.Trim();
        if (question.Length == 0)
        {
            StatusTextBlock.Text = "Enter a visual question first.";
            QuestionTextBox.Focus();
            return;
        }

        _requestInProgress = true;
        SendButton.IsEnabled = false;
        AnalyzeScreenButton.IsEnabled = false;
        VisualTeacherButton.IsEnabled = false;
        ClearChatButton.IsEnabled = false;
        MicButton.IsEnabled = false;
        QuestionTextBox.IsEnabled = false;
        StatusTextBlock.Text = "Finding that on screen...";
        QuestionTextBox.Clear();
        _visualOverlay?.Close();

        try
        {
            // V1.5.3 — Guide Me is STRUCTURED TARGET DETECTION, not chat.
            // Uses the dedicated visual-teacher endpoint and VisualGuidanceResponse
            // model. Never calls /api/ai/chat and never inserts the response into
            // the normal AI chat bubble.
            var screen = _screenCaptureService.CaptureScreenContaining(this);
            Console.WriteLine($"[GuideMe] Captured screenshot: {screen.Width} x {screen.Height}");
            Console.WriteLine("[GuideMe] Sending visual-teacher request");

            VisualGuidanceResponse response = await _backendApiClient.VisualTeacherAsync(question, screen);

            Console.WriteLine("[GuideMe] Received response");
            if (response.Target is not null)
            {
                Console.WriteLine($"[GuideMe] Target: x={response.Target.X}, y={response.Target.Y}, width={response.Target.Width}, height={response.Target.Height} (normalized)");
                Console.WriteLine($"[GuideMe] Label: {response.Label ?? "-"}");
            }
            else
            {
                Console.WriteLine("[GuideMe] Target: null (element not found on screen)");
            }
            Console.WriteLine($"[GuideMe] Confidence: {response.Confidence}");

            // No target → short status message only. No overlay, no chat bubble,
            // no invented coordinates.
            if (response.Target is null)
            {
                StatusTextBlock.Text = response.Instruction;
                return;
            }

            // Backend already gates targets below 0.5 confidence; treat as a
            // defensive no-render threshold here as well.
            if (response.Confidence < 0.5)
            {
                StatusTextBlock.Text = "I couldn't confidently identify the requested element.";
                Console.WriteLine("[GuideMe] Low confidence — no overlay rendered");
                return;
            }

            // Preserve the V1.5.2 uncertainty prefix in the overlay instruction.
            var instruction = response.Confidence < 0.75
                ? $"I'm not completely sure, but this appears to be: {response.Instruction ?? "the requested element."}"
                : response.Instruction ?? "Click the highlighted element.";
            var guidance = new VisualGuidance(
                instruction,
                response.Target,
                response.NormalizedTarget,
                response.Label,
                response.Confidence);

            // Build the monitor bounds rectangle from captured screen metadata.
            var monitorBounds = new System.Drawing.Rectangle(
                screen.MonitorLeft,
                screen.MonitorTop,
                screen.Width,
                screen.Height);

            Console.WriteLine("[GuideMe] Rendering overlay");

            _visualOverlay = new VisualOverlayWindow(guidance, monitorBounds);
            _visualOverlay.Closed += VisualOverlay_Closed;
            _visualOverlay.Show();
            StatusTextBlock.Text = "Visual guidance shown. Press Escape or wait for auto-dismiss.";
        }
        catch (Exception exception)
        {
            StatusTextBlock.Text = exception.Message;
        }
        finally
        {
            _requestInProgress = false;
            SendButton.IsEnabled = true;
            AnalyzeScreenButton.IsEnabled = true;
            VisualTeacherButton.IsEnabled = true;
            ClearChatButton.IsEnabled = true;
            MicButton.IsEnabled = true;
            QuestionTextBox.IsEnabled = true;
            QuestionTextBox.Focus();
        }
    }

    private void VisualOverlay_Closed(object? sender, EventArgs e)
    {
        if (sender is VisualOverlayWindow overlay)
        {
            overlay.Closed -= VisualOverlay_Closed;
            if (ReferenceEquals(_visualOverlay, overlay))
            {
                _visualOverlay = null;
            }
        }
    }
}