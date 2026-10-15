import express from "express";
import cors from "cors";
import dotenv from "dotenv";
import multer from "multer";
import { GroqAIService } from "./services/ai/groqAIService.js";
import { GroqSTTService, isSupportedMimeType } from "./services/ai/groqSTTService.js";
import type { ChatMessage, ChatMessageRole, VisualGuidance } from "./services/ai/aiService.js";
import {
  NO_TARGET_RESPONSE,
  NORMALIZED_COORDINATE_MAX,
  isValidVisualGuidance,
} from "./services/ai/visualGuidance.js";

dotenv.config();

const app = express();
const PORT = Number(process.env.PORT) || 3000;
const MAX_IMAGE_LENGTH = 12 * 1024 * 1024;
const MAX_CHAT_MESSAGES = 20;
const MAX_CHAT_MESSAGE_LENGTH = 4000;
const MAX_CHAT_TOTAL_LENGTH = 30_000;
const MAX_VISUAL_COORDINATE = 20_000;
const MAX_VISUAL_DIMENSION = 20_000;
const MIN_VISUAL_CONFIDENCE = 0.5;
const aiService = new GroqAIService();
const sttService = new GroqSTTService();
const allowedChatRoles = new Set<ChatMessageRole>(["system", "user", "assistant"]);

// Multer: memory storage, 10 MB limit, audio only
const MAX_AUDIO_BYTES = 10 * 1024 * 1024; // 10 MB
const audioUpload = multer({
  storage: multer.memoryStorage(),
  limits: { fileSize: MAX_AUDIO_BYTES, files: 1 },
  fileFilter(_req, file, cb) {
    if (isSupportedMimeType(file.mimetype)) {
      cb(null, true);
    } else {
      cb(new Error(`UNSUPPORTED_AUDIO_FORMAT:${file.mimetype}`));
    }
  },
});

app.use(cors());
app.use(express.json({ limit: "16mb" }));

app.get("/health", (_req, res) => {
  res.json({ status: "ok", service: "windows-ai-companion", version: "0.1.0" });
});

app.post("/api/ai/chat", async (req, res) => {
  const { messages } = req.body as { messages?: unknown };

  if (!Array.isArray(messages) || messages.length === 0) {
    res.status(400).json({ success: false, error: "At least one chat message is required." });
    return;
  }

  if (messages.length > MAX_CHAT_MESSAGES) {
    res.status(400).json({ success: false, error: "The conversation is too long." });
    return;
  }

  const validatedMessages: ChatMessage[] = [];
  let totalLength = 0;

  for (const message of messages) {
    if (typeof message !== "object" || message === null) {
      res.status(400).json({ success: false, error: "Each chat message must be an object." });
      return;
    }

    const { role, content } = message as { role?: unknown; content?: unknown };
    if (typeof role !== "string" || !allowedChatRoles.has(role as ChatMessageRole)) {
      res.status(400).json({ success: false, error: "Each chat message has an invalid role." });
      return;
    }

    if (role === "system") {
      res.status(400).json({ success: false, error: "System messages are controlled by the backend." });
      return;
    }

    if (typeof content !== "string" || content.trim().length === 0 || content.length > MAX_CHAT_MESSAGE_LENGTH) {
      res.status(400).json({ success: false, error: "Each chat message must contain valid text." });
      return;
    }

    totalLength += content.length;
    if (totalLength > MAX_CHAT_TOTAL_LENGTH) {
      res.status(400).json({ success: false, error: "The conversation content is too large." });
      return;
    }

    validatedMessages.push({ role: role as "user" | "assistant", content: content.trim() });
  }

  if (!validatedMessages.some((message) => message.role === "user")) {
    res.status(400).json({ success: false, error: "The conversation must contain a user message." });
    return;
  }

  try {
    const answer = await aiService.chat(validatedMessages);
    res.json({ success: true, answer });
  } catch (error) {
    if (error instanceof Error && error.name === "ConfigurationError") {
      res.status(503).json({ success: false, error: "The AI backend is not configured with a Groq API key." });
      return;
    }

    console.error("Chat request failed:", error instanceof Error ? error.message : "Unknown error");
    res.status(502).json({ success: false, error: "The AI service is temporarily unavailable." });
  }
});

app.post("/api/ai/analyze-screen", async (req, res) => {
  const { question, image } = req.body as { question?: unknown; image?: unknown };

  if (typeof question !== "string" || question.trim().length === 0) {
    res.status(400).json({ success: false, error: "A question is required." });
    return;
  }

  if (question.length > 2000) {
    res.status(400).json({ success: false, error: "The question is too long." });
    return;
  }

  if (typeof image !== "string" || image.length === 0 || !isValidImagePayload(image)) {
    res.status(400).json({ success: false, error: "A screen image is required." });
    return;
  }

  if (image.length > MAX_IMAGE_LENGTH) {
    res.status(413).json({ success: false, error: "The screen image is too large." });
    return;
  }

  try {
    const answer = await aiService.analyzeImage(question.trim(), image);
    res.json({ success: true, answer });
  } catch (error) {
    if (error instanceof Error && error.name === "ConfigurationError") {
      res.status(503).json({ success: false, error: "The AI backend is not configured with a Groq API key." });
      return;
    }

    console.error("Screen analysis failed:", error instanceof Error ? error.message : "Unknown error");
    res.status(502).json({ success: false, error: "The AI provider could not analyze the screen. Please try again." });
  }
});

app.post("/api/ai/visual-teacher", async (req, res) => {
  const { question, image, screenWidth, screenHeight } = req.body as {
    question?: unknown;
    image?: unknown;
    screenWidth?: unknown;
    screenHeight?: unknown;
  };

  if (typeof question !== "string" || question.trim().length === 0) {
    res.status(400).json({ success: false, error: "A visual question is required." });
    return;
  }

  if (question.length > 2000) {
    res.status(400).json({ success: false, error: "The visual question is too long." });
    return;
  }

  if (typeof image !== "string" || image.length === 0 || !isValidImagePayload(image)) {
    res.status(400).json({ success: false, error: "A screen image is required." });
    return;
  }

  if (image.length > MAX_IMAGE_LENGTH) {
    res.status(413).json({ success: false, error: "The screen image is too large." });
    return;
  }

  if (
    typeof screenWidth !== "number" ||
    typeof screenHeight !== "number" ||
    !Number.isInteger(screenWidth) ||
    !Number.isInteger(screenHeight) ||
    screenWidth <= 0 ||
    screenHeight <= 0 ||
    screenWidth > MAX_VISUAL_COORDINATE ||
    screenHeight > MAX_VISUAL_COORDINATE
  ) {
    res.status(400).json({ success: false, error: "Valid screenshot dimensions are required." });
    return;
  }

  const validatedScreenWidth = screenWidth;
  const validatedScreenHeight = screenHeight;

  // V1.5.3 — Guide Me is STRUCTURED TARGET DETECTION, not chat.
  // The AI service returns ONLY a structured target (or the canonical no-target
  // response). AI prose can never reach the desktop through this endpoint.
  try {
    console.log(`[GuideMe] User question: ${question.trim()}`);
    console.log(`[GuideMe] Sending visual-teacher request`);

    const guidance = await aiService.visualTeacher(question.trim(), image);

    if (!isValidVisualGuidance(guidance)) {
      // Fail closed: never forward a structurally invalid AI response.
      console.warn("[GuideMe] Invalid structured guidance — returning no-target response");
      res.json({ success: true, ...NO_TARGET_RESPONSE });
      return;
    }

    if (guidance.target === null) {
      console.log(`[GuideMe] No target found — instruction: ${guidance.instruction}`);
      res.json({
        success: true,
        instruction: guidance.instruction,
        target: null,
        label: null,
        confidence: guidance.confidence,
      });
      return;
    }

    if (guidance.confidence < MIN_VISUAL_CONFIDENCE) {
      console.warn(`[GuideMe] Low confidence ${guidance.confidence} — returning no-target response`);
      res.json({ success: true, ...NO_TARGET_RESPONSE });
      return;
    }

    const target = normalizedToPixels(guidance.target, validatedScreenWidth, validatedScreenHeight);
    if (!isValidPixelTarget(target, validatedScreenWidth, validatedScreenHeight)) {
      console.warn("[GuideMe] Target outside screenshot bounds — returning no-target response");
      res.json({ success: true, ...NO_TARGET_RESPONSE });
      return;
    }

    console.log(
      `[GuideMe] Received response — Target(normalized): x=${guidance.target.x}, y=${guidance.target.y}, width=${guidance.target.width}, height=${guidance.target.height}`,
    );
    console.log(`[GuideMe] Label: ${guidance.label} | Confidence: ${guidance.confidence}`);

    res.json({
      success: true,
      instruction: guidance.instruction,
      target,
      normalizedTarget: guidance.target,
      label: guidance.label,
      confidence: guidance.confidence,
    });
  } catch (error) {
    if (error instanceof Error && error.name === "ConfigurationError") {
      res.status(503).json({ success: false, error: "The AI backend is not configured with a Groq API key." });
      return;
    }

    console.error("Visual teacher request failed:", error instanceof Error ? error.message : "Unknown error");
    res.json({ success: true, ...NO_TARGET_RESPONSE });
  }
});

app.post("/api/ai/transcribe", (req, res, next) => {
  // Run multer as a single-field upload, then hand off to the async handler
  audioUpload.single("audio")(req, res, (multerError) => {
    if (multerError) {
      if (multerError instanceof multer.MulterError && multerError.code === "LIMIT_FILE_SIZE") {
        res.status(413).json({ success: false, message: "The audio file is too large (maximum 10 MB)." });
        return;
      }
      if (multerError instanceof Error && multerError.message.startsWith("UNSUPPORTED_AUDIO_FORMAT:")) {
        res.status(415).json({ success: false, message: "Unsupported audio format. Please use WAV, MP3, OGG, or WebM." });
        return;
      }
      next(multerError);
      return;
    }

    // Hand off to async route logic
    handleTranscribe(req as express.Request & { file?: Express.Multer.File }, res).catch(next);
  });
});

async function handleTranscribe(
  req: express.Request & { file?: Express.Multer.File },
  res: express.Response
): Promise<void> {
  if (!req.file) {
    res.status(400).json({ success: false, message: "An audio file is required in the 'audio' field." });
    return;
  }

  const { buffer, mimetype, originalname, size } = req.file;

  // Validate size (belt-and-suspenders — multer already checked, but be explicit)
  if (size > MAX_AUDIO_BYTES) {
    res.status(413).json({ success: false, message: "The audio file is too large (maximum 10 MB)." });
    return;
  }

  // Validate language param if provided
  let language: string | undefined;
  if (typeof req.body?.language === "string" && req.body.language.trim().length > 0) {
    const langValue = req.body.language.trim();
    // ISO 639-1: 2-letter code, optionally with region e.g. "en", "zh-CN"
    if (/^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,4})?$/.test(langValue)) {
      language = langValue.toLowerCase();
    }
    // If invalid, silently ignore — Whisper auto-detects
  }

  try {
    const result = await sttService.transcribe(
      buffer,
      mimetype,
      originalname ?? "recording.wav",
      language !== undefined ? { language } : {}
    );
    const text = result.text.trim();

    if (text.length === 0) {
      res.json({ success: false, message: "I couldn't hear anything. Please try again." });
      return;
    }

    res.json({ success: true, text });
  } catch (error) {
    if (error instanceof Error && error.name === "ConfigurationError") {
      res.status(503).json({ success: false, message: "Voice transcription is not configured on this server." });
      return;
    }

    if (error instanceof Error && error.name === "ValidationError") {
      res.status(415).json({ success: false, message: "Unsupported audio format." });
      return;
    }

    // Detect Groq rate limit or model unavailability
    const message = error instanceof Error ? error.message : "";
    if (message.includes("rate_limit") || message.includes("429")) {
      res.status(429).json({ success: false, message: "Voice transcription is temporarily rate-limited. Please try again in a moment." });
      return;
    }

    if (message.includes("model") && (message.includes("not found") || message.includes("unavailable") || message.includes("404"))) {
      res.status(503).json({ success: false, message: "The speech-to-text model is unavailable. Please check GROQ_STT_MODEL in the server configuration." });
      return;
    }

    // Log the error class/message but NOT audio data
    console.error("Transcription request failed:", error instanceof Error ? error.message : "Unknown error");
    res.status(502).json({ success: false, message: "Voice transcription is currently unavailable. Please try again." });
  }
}

app.use((error: unknown, _req: express.Request, res: express.Response, next: express.NextFunction) => {
  if (error instanceof SyntaxError) {
    res.status(400).json({ success: false, error: "The request body is not valid JSON." });
    return;
  }

  if (typeof error === "object" && error !== null && "type" in error && error.type === "entity.too.large") {
    res.status(413).json({ success: false, error: "The request body is too large." });
    return;
  }

  next(error);
});

app.listen(PORT, () => {
  console.log(`Windows AI Companion backend running on http://localhost:${PORT}`);
});

function normalizedToPixels(target: Exclude<VisualGuidance["target"], null>, screenWidth: number, screenHeight: number) {
  return {
    x: Math.round(target.x / NORMALIZED_COORDINATE_MAX * screenWidth),
    y: Math.round(target.y / NORMALIZED_COORDINATE_MAX * screenHeight),
    width: Math.max(1, Math.round(target.width / NORMALIZED_COORDINATE_MAX * screenWidth)),
    height: Math.max(1, Math.round(target.height / NORMALIZED_COORDINATE_MAX * screenHeight)),
  };
}

function isValidPixelTarget(target: ReturnType<typeof normalizedToPixels>, screenWidth: number, screenHeight: number): boolean {
  return (
    target.x >= 0 &&
    target.y >= 0 &&
    target.x < screenWidth &&
    target.y < screenHeight &&
    target.width > 0 &&
    target.height > 0 &&
    target.x + target.width <= screenWidth &&
    target.y + target.height <= screenHeight &&
    target.width <= MAX_VISUAL_DIMENSION &&
    target.height <= MAX_VISUAL_DIMENSION
  );
}

function isValidImagePayload(image: string): boolean {
  const base64 = image.startsWith("data:") ? image.split(",", 2)[1] : image;
  return (
    typeof base64 === "string" &&
    base64.length >= 16 &&
    base64.length % 4 === 0 &&
    /^[A-Za-z0-9+/]+={0,2}$/.test(base64)
  );
}