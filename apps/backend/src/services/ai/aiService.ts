export type ChatMessageRole = "system" | "user" | "assistant";

export interface ChatMessage {
  role: ChatMessageRole;
  content: string;
}

export interface VisualTarget {
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * Structured Guide Me target. Internal backend representation; `label` is
 * null exactly when `target` is null (canonical no-target response).
 */
export interface VisualGuidance {
  instruction: string;
  label: string | null;
  target: VisualTarget | null;
  confidence: number;
}

export interface AIService {
  analyzeImage(question: string, image: string): Promise<string>;
  chat(messages: ChatMessage[]): Promise<string>;
  /**
   * V1.5.3 — Guide Me target detection. Returns ONLY a structured target:
   * never AI prose, never a chat-style answer. When the requested element is
   * not visible the target is null (canonical NO_TARGET_RESPONSE).
   */
  visualTeacher(question: string, image: string): Promise<VisualGuidance>;
}