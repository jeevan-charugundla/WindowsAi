using System.IO;
using NAudio.Wave;

namespace WindowsAICompanion.Desktop;

/// <summary>
/// Result returned after a recording session completes.
/// </summary>
internal sealed class AudioRecordingResult
{
    /// <summary>Raw WAV bytes ready for upload. Null when recording failed or produced no audio.</summary>
    public byte[]? AudioBytes { get; init; }

    /// <summary>Friendly error message when recording was unsuccessful.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>True when audio was captured successfully and AudioBytes is non-null.</summary>
    public bool Success => AudioBytes is not null && AudioBytes.Length > 0;
}

/// <summary>
/// Captures microphone audio on demand (push-to-record).
///
/// Privacy contract:
///   - The microphone is only opened when <see cref="StartAsync"/> is called.
///   - Recording stops immediately when <see cref="StopAndGetResultAsync"/> is called
///     or when the 30-second maximum duration is reached.
///   - Captured audio is held in memory only long enough to be uploaded.
///   - No audio is written to disk, logged, or stored permanently.
/// </summary>
internal sealed class MicrophoneRecorder : IDisposable
{
    // V1.6: 30-second maximum to prevent runaway recordings
    private static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(30);

    // PCM 16 kHz mono — broadly compatible and small
    private static readonly WaveFormat RecordingFormat = new(rate: 16_000, bits: 16, channels: 1);

    private WaveInEvent? _waveIn;
    private MemoryStream? _audioStream;
    private WaveFileWriter? _waveWriter;
    private CancellationTokenSource? _maxDurationCts;
    private TaskCompletionSource<bool>? _stoppedTcs;
    private bool _isRecording;
    private readonly object _lock = new();

    /// <summary>
    /// Raised when the maximum recording duration (30 s) is reached and recording
    /// has been stopped automatically.  Subscribe before calling StartAsync.
    /// </summary>
    public event EventHandler? MaxDurationReached;

    /// <summary>True while recording is in progress.</summary>
    public bool IsRecording
    {
        get { lock (_lock) { return _isRecording; } }
    }

    /// <summary>
    /// Opens the default microphone and begins capturing audio.
    /// Returns false (with a message) if the microphone is unavailable.
    /// </summary>
    public Task<(bool success, string? errorMessage)> StartAsync()
    {
        lock (_lock)
        {
            if (_isRecording)
                return Task.FromResult<(bool, string?)>((false, "Recording is already in progress."));

            try
            {
                // WaveInEvent uses the Windows audio stack; device 0 = default mic
                _waveIn = new WaveInEvent
                {
                    WaveFormat = RecordingFormat,
                    BufferMilliseconds = 100,
                    DeviceNumber = 0
                };

                _audioStream = new MemoryStream();
                _waveWriter = new WaveFileWriter(_audioStream, RecordingFormat);
                _stoppedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

                _waveIn.DataAvailable += WaveIn_DataAvailable;
                _waveIn.RecordingStopped += WaveIn_RecordingStopped;
                _waveIn.StartRecording();

                _isRecording = true;

                // Enforce maximum duration
                _maxDurationCts = new CancellationTokenSource();
                _ = EnforceMaxDurationAsync(_maxDurationCts.Token);

                return Task.FromResult<(bool, string?)>((true, null));
            }
            catch (Exception ex)
            {
                DisposeRecordingObjects();
                var msg = IsMicrophonePermissionError(ex)
                    ? "Microphone access is unavailable. Check Windows microphone permissions."
                    : "Could not start the microphone. Please check that a microphone is connected.";
                return Task.FromResult<(bool, string?)>((false, msg));
            }
        }
    }

    /// <summary>
    /// Stops recording and returns the captured audio.
    /// Safe to call even if recording has already stopped.
    /// </summary>
    public async Task<AudioRecordingResult> StopAndGetResultAsync()
    {
        TaskCompletionSource<bool>? tcs;

        lock (_lock)
        {
            if (!_isRecording || _waveIn is null)
            {
                return new AudioRecordingResult { ErrorMessage = "No active recording." };
            }

            // Cancel the max-duration timer
            _maxDurationCts?.Cancel();
            tcs = _stoppedTcs;

            try
            {
                // StopRecording is async internally; RecordingStopped fires when buffers flush
                _waveIn.StopRecording();
            }
            catch
            {
                // Ignore stop errors — we collect whatever audio we have
            }
        }

        // Wait for RecordingStopped to fire (with a safety timeout)
        if (tcs is not null)
        {
            await Task.WhenAny(tcs.Task, Task.Delay(3_000)).ConfigureAwait(false);
        }

        return ExtractResult();
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task EnforceMaxDurationAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(MaxDuration, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation when user stops before 30 s
            return;
        }

        // 30 seconds reached — stop automatically
        bool wasRecording;
        lock (_lock) { wasRecording = _isRecording; }

        if (wasRecording)
        {
            MaxDurationReached?.Invoke(this, EventArgs.Empty);
            // StopRecording must be called; result collected by the UI handler
            lock (_lock)
            {
                try { _waveIn?.StopRecording(); } catch { /* ignore */ }
            }
        }
    }

    private void WaveIn_DataAvailable(object? sender, WaveInEventArgs e)
    {
        lock (_lock)
        {
            if (_waveWriter is null || !_isRecording) return;
            try
            {
                _waveWriter.Write(e.Buffer, 0, e.BytesRecorded);
            }
            catch
            {
                // Ignore write errors in the audio callback
            }
        }
    }

    private void WaveIn_RecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_lock)
        {
            _isRecording = false;
            try { _waveWriter?.Flush(); } catch { /* ignore */ }
        }

        _stoppedTcs?.TrySetResult(true);
    }

    private AudioRecordingResult ExtractResult()
    {
        lock (_lock)
        {
            try
            {
                if (_audioStream is null || _audioStream.Length == 0)
                {
                    return new AudioRecordingResult { ErrorMessage = "No audio was captured." };
                }

                // Finalise the WAV header and return the bytes
                _waveWriter?.Flush();
                var bytes = _audioStream.ToArray();

                // Minimum sanity check: WAV header is 44 bytes; anything shorter is empty
                const int WavHeaderSize = 44;
                if (bytes.Length <= WavHeaderSize)
                {
                    return new AudioRecordingResult { ErrorMessage = "No audio was captured." };
                }

                return new AudioRecordingResult { AudioBytes = bytes };
            }
            catch (Exception ex)
            {
                return new AudioRecordingResult
                {
                    ErrorMessage = $"Could not read the recorded audio: {ex.Message}"
                };
            }
            finally
            {
                DisposeRecordingObjects();
            }
        }
    }

    private void DisposeRecordingObjects()
    {
        // Called inside lock
        _maxDurationCts?.Cancel();
        _maxDurationCts?.Dispose();
        _maxDurationCts = null;

        if (_waveIn is not null)
        {
            _waveIn.DataAvailable -= WaveIn_DataAvailable;
            _waveIn.RecordingStopped -= WaveIn_RecordingStopped;
            _waveIn.Dispose();
            _waveIn = null;
        }

        _waveWriter?.Dispose();
        _waveWriter = null;

        _audioStream?.Dispose();
        _audioStream = null;
    }

    private static bool IsMicrophonePermissionError(Exception ex)
    {
        // NAudio surfaces permission denial as MmException with MmResult codes,
        // or as InvalidOperationException with "BadDeviceId" or similar messages.
        var message = ex.Message.ToLowerInvariant();
        return message.Contains("access denied") ||
               message.Contains("permission") ||
               message.Contains("baddeviceid") ||
               message.Contains("nodriver") ||
               ex is UnauthorizedAccessException;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            DisposeRecordingObjects();
        }
    }
}
