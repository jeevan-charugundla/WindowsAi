import OpenAI from "openai";
import type { AIService, ChatMessage, VisualGuidance } from "./aiService.js";
import { NO_TARGET_RESPONSE, requestVisualTarget } from "./visualGuidance.js";

const GROQ_BASE_URL = "https://api.groq.com/openai/v1";
const DEFAULT_MODEL = "qwen/qwen3.8-27b";
const SYSTEM_PROMPT = "You are Windows AI Companion, a helpful AI assistant running on Windows. Give clear, concise, useful answers. When discussing the user's screen, only describe information visible in the supplied image.";

function configurationError(): Error {
  const error = new Error("GROQ_API_KEY is not configured.");
  error.name = "ConfigurationError";
  return error;
}

export class GroqAIService implements AIService {
  private readonly apiKey = process.env.GROQ_API_KEY?.trim();
  private readonly model = process.env.GROQ_MODEL?.trim() || DEFAULT_MODEL;
  private client: OpenAI | undefined;

  async analyzeImage(question: string, image: string): Promise<string> {
    if (!this.apiKey) {
      throw configurationError();
    }

    this.client ??= new OpenAI({
      apiKey: this.apiKey,
      baseURL: GROQ_BASE_URL,
      timeout: 45_000,
      maxRetries: 1,
    });

    const imageUrl = image.startsWith("data:")
      ? image
      : `data:image/png;base64,${image}`;

    const completion = await this.client.chat.completions.create({
      model: this.model,
      temperature: 0.2,
      max_tokens: 700,
      messages: [
        { role: "system", content: SYSTEM_PROMPT },
        {
          role: "user",
          content: [
            { type: "text", text: question },
            { type: "image_url", image_url: { url: imageUrl } },
          ],
        },
      ],
    });

    const answer = completion.choices[0]?.message.content;
    if (!answer || typeof answer !== "string") {
      throw new Error("The AI provider returned an empty response.");
    }

    return answer.trim();
  }

  async chat(messages: ChatMessage[]): Promise<string> {
    if (!this.apiKey) {
      throw configurationError();
    }

    this.client ??= new OpenAI({
      apiKey: this.apiKey,
      baseURL: GROQ_BASE_URL,
      timeout: 45_000,
      maxRetries: 1,
    });

    const completion = await this.client.chat.completions.create({
      model: this.model,
      temperature: 0.2,
      max_tokens: 700,
      messages: [
        { role: "system", content: SYSTEM_PROMPT },
        ...messages.map((message) => ({
          role: message.role as "user" | "assistant",
          content: message.content,
        })),
      ],
    });

    const answer = completion.choices[0]?.message.content;
    if (!answer || typeof answer !== "string") {
      throw new Error("The AI provider returned an empty response.");
    }

    return answer.trim();
  }

  /**
   * V1.5.3 — Guide Me visual target detection.
   *
   * Uses the strict structured-output pipeline in visualGuidance.ts:
   * the model must return ONLY the target JSON object, which is parsed and
   * validated before it can reach the desktop. A missing/unidentifiable
   * element yields the canonical NO_TARGET_RESPONSE (target: null) — never
   * conversational prose and never invented coordinates.
   */
  async visualTeacher(question: string, image: string): Promise<VisualGuidance> {
    if (!this.apiKey) {
      throw configurationError();
    }

    try {
      return await requestVisualTarget(
        {
          apiKey: this.apiKey,
          model: this.model,
          timeoutMs: 45_000,
          maxRetries: 1,
        },
        question,
        image,
      );
    } catch (error) {
      if (error instanceof Error && error.name === "ConfigurationError") {
        throw error;
      }
      // Fail closed: any unexpected model/transport failure becomes the
      // canonical no-target response so the desktop can never receive AI prose.
      console.error("Visual teacher target detection failed:", error instanceof Error ? error.message : "Unknown error");
      return { ...NO_TARGET_RESPONSE };
    }
  }
}
