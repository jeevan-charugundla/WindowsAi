import OpenAI from "openai";
import type { VisualGuidance } from "./aiService.js";

// ─────────────────────────────────────────────────────────────────────────────
// Constants (V1.5.3 — Guide Me structured target detection)
// ─────────────────────────────────────────────────────────────────────────────

export const NORMALIZED_COORDINATE_MAX = 1000;

/**
 * Strict system prompt for the Guide Me / visual-teacher feature.
 * The model is a target DETECTOR, not a chatbot: it must return only the
 * required JSON object and never a natural-language explanation.
 */
export const VISUAL_TEACHER_PROMPT = `You are the visual target detector for a Windows AI assistant.

Your task is to identify the exact visible UI element requested by the user in the supplied screenshot.

You are NOT a general chatbot.

You must NOT explain the screenshot.

You must NOT write a long natural-language answer.

You must return ONLY the required JSON object.

First determine whether the requested target is actually visible.

If it is visible:
- identify the smallest useful bounding box around the requested UI element
- return normalized x, y, width and height values from 0 to 1000
- provide a short instruction
- provide a concise label
- provide confidence between 0 and 1

Coordinate precision rules:
- x is measured from the LEFT edge of the image, y from the TOP edge.
- x = 0 means the leftmost pixel column and x = 1000 means the rightmost.
- Before answering, estimate the element's center as a percentage of the
  image width and height, then convert those percentages to 0-1000 values.
- Small elements (short labels, small buttons) still occupy a real fraction
  of the image. A word sitting 3% from the left edge has x near 30, not 0.
- Never collapse a visible element to a single point: width and height must
  fully cover the element's text or icon plus a small margin.

If it is not visible:
- target must be null
- label must be null
- confidence must be 0 or appropriately low
- do not invent a location

The target must refer to the exact requested UI element, not a nearby element.

Verification step before answering:
1. In sceneReading, list LEFT TO RIGHT every visible label in the row (or
   region) that contains the requested element. For each label give the exact
   visible text AND its normalized bounding box (0-1000). This is a
   set-of-marks pass: you must read and box every label you see.
2. Match the user's requested element against that list by its TEXT.
3. The target must be the bounding box of the matched entry from your own
   sceneReading list - never a separately estimated location.
If no label text matches, the element is not visible.

For example:
If the user asks for 'File', target File.
If the user asks for 'View', target View.
If the user asks for 'Close', target the actual Close button.
Do not target the entire application window.

Return ONLY valid JSON.`;

/**
 * JSON schema enforced via Groq structured output (response_format json_schema)
 * with an HTTP fallback when the configured model does not support it.
 */
export const VISUAL_TARGET_SCHEMA = {
  name: "visual_target",
  strict: true,
  schema: {
    type: "object",
    additionalProperties: false,
    required: ["instruction", "sceneReading", "target", "label", "confidence"],
    properties: {
      instruction: { type: "string" },
      sceneReading: {
        type: "array",
        items: {
          type: "object",
          additionalProperties: false,
          required: ["text", "x", "y", "width", "height"],
          properties: {
            text: { type: "string" },
            x: { type: "number" },
            y: { type: "number" },
            width: { type: "number" },
            height: { type: "number" },
          },
        },
      },
      target: {
        type: ["object", "null"],
        additionalProperties: false,
        properties: {
          x: { type: "number" },
          y: { type: "number" },
          width: { type: "number" },
          height: { type: "number" },
        },
        required: ["x", "y", "width", "height"],
      },
      label: { type: ["string", "null"] },
      confidence: { type: "number" },
    },
  },
} as const;

/**
 * Canonical no-target response (STEP 3). Used when the element genuinely does
 * not exist on screen, when parsing fails, or when validation rejects the AI
 * response. Never invents coordinates; never contains AI prose.
 */
export const NO_TARGET_RESPONSE = {
  instruction: "I cannot find the requested element on the current screen.",
  target: null,
  label: null,
  confidence: 0,
} as const;

// ─────────────────────────────────────────────────────────────────────────────
// Response parsing (strict + lenient fallback)
// ─────────────────────────────────────────────────────────────────────────────

/**
 * Extracts the first JSON object from a raw model response. Handles markdown
 * code fences, <think> blocks from reasoning models, and surrounding prose.
 */
export function extractJsonCandidate(raw: string): string {
  let text = raw.trim();

  // Strip <think>...</think> reasoning blocks emitted by reasoning models.
  const thinkEnd = text.lastIndexOf("</think>");
  if (thinkEnd >= 0) {
    text = text.slice(thinkEnd + "</think>".length).trim();
  }

  // Strip a single leading markdown code fence (``` or ```json).
  const fenceStart = text.match(/^```(?:json)?\s*/i);
  const fence = fenceStart?.[0];
  if (fence !== undefined) {
    text = text.slice(fence.length);
    const fenceEnd = text.lastIndexOf("```");
    if (fenceEnd >= 0) {
      text = text.slice(0, fenceEnd);
    }
  }

  const start = text.indexOf("{");
  const end = text.lastIndexOf("}");
  if (start < 0 || end <= start) {
    return "";
  }
  return text.slice(start, end + 1);
}

/**
 * Coerces an unknown parsed value into a target object or null.
 * Numbers are floored to integers (models may emit floats like 35.5).
 */
export function coerceTarget(value: unknown): { x: number; y: number; width: number; height: number } | null {
  if (value === null || value === undefined) {
    return null;
  }
  if (typeof value !== "object") {
    return null;
  }

  const record = value as Record<string, unknown>;
  const readCoordinate = (key: string): number => {
    const raw = record[key];
    if (typeof raw === "number" && Number.isFinite(raw)) {
      return Math.floor(raw);
    }
    if (typeof raw === "string" && raw.trim() !== "" && Number.isFinite(Number(raw))) {
      return Math.floor(Number(raw));
    }
    return Number.NaN;
  };

  const x = readCoordinate("x");
  const y = readCoordinate("y");
  const width = readCoordinate("width");
  const height = readCoordinate("height");

  if ([x, y, width, height].some((coordinate) => !Number.isFinite(coordinate))) {
    return null;
  }

  return { x, y, width, height };
}

const isValidLabelCandidate = (value: unknown): value is string =>
  typeof value === "string" && value.trim().length > 0 && value.trim().length <= 100 && !/[<>\u0000-\u001F]/.test(value.trim());

const toFiniteNumber = (value: unknown): number | null => {
  if (typeof value === "number" && Number.isFinite(value)) {
    return value;
  }
  if (typeof value === "string" && value.trim() !== "" && Number.isFinite(Number(value))) {
    return Number(value);
  }
  return null;
};

// ─────────────────────────────────────────────────────────────────────────────
// Set-of-marks resolution (V1.5.3)
//
// Vision models read text far more reliably than they estimate absolute
// coordinates. The schema therefore forces a sceneReading pass - every visible
// label in the relevant region WITH its bounding box - and the backend then
// deterministically selects the box whose text matches the user's request.
// This fixes neighbor-confusion (e.g. "Tools" landing on "Help"'s slot).
// ─────────────────────────────────────────────────────────────────────────────

interface SceneMark {
  text: string;
  x: number;
  y: number;
  width: number;
  height: number;
}

/** Parses the model's sceneReading array into grid-valid marks (skips junk). */
function coerceSceneMarks(value: unknown): SceneMark[] {
  if (!Array.isArray(value)) {
    return [];
  }

  const marks: SceneMark[] = [];
  for (const entry of value) {
    if (typeof entry !== "object" || entry === null) {
      continue;
    }
    const record = entry as Record<string, unknown>;
    const text = typeof record.text === "string" ? record.text.trim() : "";
    if (text.length === 0 || text.length > 100) {
      continue;
    }
    const box = coerceTarget({ x: record.x, y: record.y, width: record.width, height: record.height });
    if (box === null || box.width <= 0 || box.height <= 0) {
      continue;
    }
    if (
      box.x < 0 ||
      box.y < 0 ||
      box.x + box.width > NORMALIZED_COORDINATE_MAX ||
      box.y + box.height > NORMALIZED_COORDINATE_MAX
    ) {
      continue;
    }
    marks.push({ text, x: box.x, y: box.y, width: box.width, height: box.height });
  }
  return marks;
}

const normalizeText = (text: string): string =>
  text
    .toLowerCase()
    .replace(/[^a-z0-9\s]/g, " ")
    .replace(/\s+/g, " ")
    .trim();

/** Words ignored when extracting what the user is looking for. */
const QUERY_STOP_WORDS = new Set([
  "where", "is", "the", "a", "an", "on", "in", "at", "of", "to", "please",
  "find", "show", "me", "this", "that", "it", "its", "i", "you", "can",
  "tell", "do", "my", "screen", "app", "application", "element", "ui",
  "menu", "button", "icon", "tab", "field", "box", "link", "bar", "top",
]);

/** Extracts the meaningful search words from the question (and model label). */
function queryKeywords(question: string, label: string | null): string[] {
  const combined = label ? `${label} ${question}` : question;
  const words = normalizeText(combined)
    .split(" ")
    .filter((word) => word.length > 0 && !QUERY_STOP_WORDS.has(word));
  return [...new Set(words)];
}

/**
 * Deterministically picks the mark whose text matches one of the request's
 * keywords (word-level match, so "File" never matches "Profile").
 */
function resolveTargetFromSceneMarks(marks: SceneMark[], keywords: string[]): SceneMark | null {
  if (keywords.length === 0) {
    return null;
  }
  for (const mark of marks) {
    const markWords = normalizeText(mark.text).split(" ");
    if (keywords.some((keyword) => markWords.includes(keyword))) {
      return mark;
    }
  }
  return null;
}

/**
 * Parses and validates a raw model response into VisualGuidance.
 *
 * Target resolution order:
 * 1. Set-of-marks: when the model's sceneReading contains a label whose text
 *    matches the user's request, THAT mark's bounding box is used (fixes
 *    neighbor-confusion and improves localization for small elements).
 * 2. Fallback: the model's own reported target, after grid validation.
 *
 * Fail-closed contract (garbage in → no-target out, never a guessed location):
 * - model explicitly reports target null → canonical no-target response
 *   (a short, safe instruction from the model may be preserved)
 * - malformed JSON, missing target, out-of-range coordinates, missing label →
 *   canonical NO_TARGET_RESPONSE with the canonical instruction, since the
 *   model's own text cannot be trusted when the structure is broken
 */
export function parseVisualGuidance(rawAnswer: string, question: string = ""): VisualGuidance {
  const candidate = extractJsonCandidate(rawAnswer);
  if (candidate.length === 0) {
    return { ...NO_TARGET_RESPONSE };
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(candidate);
  } catch {
    return { ...NO_TARGET_RESPONSE };
  }

  if (typeof parsed !== "object" || parsed === null) {
    return { ...NO_TARGET_RESPONSE };
  }

  const value = parsed as Record<string, unknown>;

  const hasTrustedStructure =
    typeof value.instruction === "string" && value.instruction.trim().length > 0 && value.instruction.trim().length <= 300;

  const instruction =
    hasTrustedStructure ? (value.instruction as string).trim() : NO_TARGET_RESPONSE.instruction;

  const labelCandidate: unknown = value.label;
  const label = isValidLabelCandidate(labelCandidate) ? labelCandidate.trim() : null;

  let confidence = toFiniteNumber(value.confidence);
  if (confidence === null || confidence < 0 || confidence > 1) {
    confidence = 0;
  }

  const targetValue: unknown = value.target;

  // The model may deliberately report "not found" via an explicit JSON null.
  // A MISSING target key, by contrast, means the object structure is broken —
  // the model's text cannot be trusted and the canonical response is used.
  const explicitlyNullTarget = targetValue === null;

  if (explicitlyNullTarget) {
    return { ...NO_TARGET_RESPONSE, instruction: hasTrustedStructure ? instruction : NO_TARGET_RESPONSE.instruction };
  }

  // ── Set-of-marks resolution ─────────────────────────────────────────────
  // Prefer the bounding box of the sceneReading entry whose text matches the
  // user's request over the model's separately estimated target coordinates.
  const marks = coerceSceneMarks(value.sceneReading);
  if (marks.length > 0) {
    const keywords = queryKeywords(question, label);
    const matchedMark = resolveTargetFromSceneMarks(marks, keywords);
    if (matchedMark) {
      return {
        instruction,
        label: matchedMark.text,
        target: { x: matchedMark.x, y: matchedMark.y, width: matchedMark.width, height: matchedMark.height },
        confidence: confidence > 0 ? confidence : 0.5,
      };
    }
    // No sceneReading text matches the request: fall back to the model's own
    // target below (it may have boxed a region outside its reading pass).
  }

  const target = coerceTarget(targetValue);

  // Missing or unparseable target object → canonical no-target response.
  if (target === null) {
    return { ...NO_TARGET_RESPONSE };
  }

  // Target requested → apply grid validation. Anything out of range is
  // rejected wholesale (fail-closed) rather than clamped, and the model's
  // instruction is not trusted when the structure failed validation.
  const { x, y, width, height } = target;
  const inGrid =
    x >= 0 &&
    y >= 0 &&
    width > 0 &&
    height > 0 &&
    x <= NORMALIZED_COORDINATE_MAX &&
    y <= NORMALIZED_COORDINATE_MAX &&
    x + width <= NORMALIZED_COORDINATE_MAX &&
    y + height <= NORMALIZED_COORDINATE_MAX;

  if (!inGrid) {
    return { ...NO_TARGET_RESPONSE };
  }

  // A label is required whenever a target is reported.
  if (label === null) {
    return { ...NO_TARGET_RESPONSE };
  }

  return { instruction, label, target, confidence };
}

/**
 * Server-side validation of a guidance object (STEP 6). Mirrors the JSON
 * schema rules so a hand-crafted or fallback-path response is still checked.
 */
export function isValidVisualGuidance(guidance: VisualGuidance): boolean {
  const { instruction, target, confidence } = guidance;

  if (typeof instruction !== "string" || instruction.trim().length === 0 || instruction.length > 300) {
    return false;
  }

  if (!Number.isFinite(confidence) || confidence < 0 || confidence > 1) {
    return false;
  }

  if (target === null) {
    return true; // no-target responses are valid (instruction + confidence checked above)
  }

  if (typeof guidance.label !== "string" || guidance.label.trim().length === 0 || guidance.label.length > 100) {
    return false;
  }

  return (
    Number.isInteger(target.x) &&
    Number.isInteger(target.y) &&
    Number.isInteger(target.width) &&
    Number.isInteger(target.height) &&
    target.x >= 0 &&
    target.y >= 0 &&
    target.x <= NORMALIZED_COORDINATE_MAX &&
    target.y <= NORMALIZED_COORDINATE_MAX &&
    target.width > 0 &&
    target.height > 0 &&
    target.width <= NORMALIZED_COORDINATE_MAX &&
    target.height <= NORMALIZED_COORDINATE_MAX &&
    target.x + target.width <= NORMALIZED_COORDINATE_MAX &&
    target.y + target.height <= NORMALIZED_COORDINATE_MAX
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Groq Vision call
// ─────────────────────────────────────────────────────────────────────────────

const GROQ_BASE_URL = "https://api.groq.com/openai/v1";

interface GroqClientOptions {
  apiKey: string;
  model: string;
  timeoutMs: number;
  maxRetries: number;
}

/**
 * Calls Groq Vision with the strict target-detection prompt and structured
 * output. Falls back to plain HTTP JSON mode when the configured model does
 * not support json_schema response formats. The raw text is ALWAYS parsed and
 * validated with parseVisualGuidance before it can reach the desktop.
 */
export async function requestVisualTarget(
  options: GroqClientOptions,
  question: string,
  image: string,
): Promise<VisualGuidance> {
  const client = new OpenAI({
    apiKey: options.apiKey,
    baseURL: GROQ_BASE_URL,
    timeout: options.timeoutMs,
    maxRetries: options.maxRetries,
  });

  const imageUrl = image.startsWith("data:") ? image : `data:image/png;base64,${image}`;

  const messages = [
    { role: "system" as const, content: VISUAL_TEACHER_PROMPT },
    {
      role: "user" as const,
      content: [
        { type: "text" as const, text: `Where is: ${question}` },
        { type: "image_url" as const, image_url: { url: imageUrl } },
      ],
    },
  ];

  const baseParams = {
    model: options.model,
    temperature: 0,
    max_tokens: 600,
    messages,
  } as const;

  let rawAnswer: string | undefined;

  // Preferred path: native structured output guarantees valid JSON shapes.
  try {
    const completion = await client.chat.completions.create({
      ...baseParams,
      response_format: { type: "json_schema", json_schema: VISUAL_TARGET_SCHEMA },
    });
    rawAnswer = completion.choices[0]?.message?.content ?? undefined;
  } catch {
    // Fallback path: plain JSON mode (some models reject json_schema).
    const completion = await client.chat.completions.create({
      ...baseParams,
      response_format: { type: "json_object" },
    });
    rawAnswer = completion.choices[0]?.message?.content ?? undefined;
  }

  if (!rawAnswer || typeof rawAnswer !== "string") {
    throw new Error("The AI provider returned an empty visual target.");
  }

  return parseVisualGuidance(rawAnswer, question);
}
