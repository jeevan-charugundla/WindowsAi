import OpenAI, { toFile } from "openai";

const GROQ_BASE_URL = "https://api.groq.com/openai/v1";
const DEFAULT_STT_MODEL = "whisper-large-v3-turbo";

// Groq-supported audio MIME types for Whisper
const SUPPORTED_AUDIO_MIME_TYPES = new Set([
  "audio/wav",
  "audio/wave",
  "audio/x-wav",
  "audio/mpeg",
  "audio/mp3",
  "audio/mp4",
  "audio/ogg",
  "audio/webm",
  "audio/flac",
  "audio/x-flac",
  "audio/aac",
  "audio/x-m4a",
  "audio/m4a",
]);

export interface TranscribeOptions {
  /** ISO 639-1 language code, e.g. "en". Optional — Whisper auto-detects if omitted. */
  language?: string;
}

export interface TranscribeResult {
  text: string;
}

export class GroqSTTService {
  private readonly apiKey = process.env.GROQ_API_KEY?.trim();
  private readonly sttModel = process.env.GROQ_STT_MODEL?.trim() || DEFAULT_STT_MODEL;
  private client: OpenAI | undefined;

  /**
   * Transcribe an audio buffer via Groq Whisper.
   * @param audioBuffer  Raw audio bytes (WAV/MP3/etc.)
   * @param mimeType     MIME type of the audio (e.g. "audio/wav")
   * @param filename     Filename hint sent to the API (e.g. "recording.wav")
   * @param options      Optional language hint
   */
  async transcribe(
    audioBuffer: Buffer,
    mimeType: string,
    filename: string,
    options: TranscribeOptions = {}
  ): Promise<TranscribeResult> {
    if (!this.apiKey) {
      const error = new Error("GROQ_API_KEY is not configured.");
      error.name = "ConfigurationError";
      throw error;
    }

    if (!isSupportedMimeType(mimeType)) {
      const error = new Error(`Unsupported audio format: ${mimeType}`);
      error.name = "ValidationError";
      throw error;
    }

    this.client ??= new OpenAI({
      apiKey: this.apiKey,
      baseURL: GROQ_BASE_URL,
      timeout: 60_000,
      maxRetries: 1,
    });

    // Wrap the buffer as a File-like object the OpenAI SDK understands
    const audioFile = await toFile(audioBuffer, filename, { type: mimeType });

    const transcription = await this.client.audio.transcriptions.create({
      model: this.sttModel,
      file: audioFile,
      response_format: "json",
      ...(options.language ? { language: options.language } : {}),
    });

    const text = transcription.text?.trim() ?? "";
    return { text };
  }
}

export function isSupportedMimeType(mimeType: string): boolean {
  // Normalise — strip parameters like "; codecs=opus"
  const base = mimeType.split(";")[0]?.trim().toLowerCase() ?? "";
  return SUPPORTED_AUDIO_MIME_TYPES.has(base);
}
