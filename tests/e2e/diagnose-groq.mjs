/**
 * Diagnostic: calls Groq Vision directly with the V1.5.3 schema and prints the
 * full structured output (including sceneReading) to understand localization
 * errors. Reads GROQ_API_KEY / GROQ_MODEL from apps/backend/.env.
 */
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const envText = readFileSync(join(here, "..", "..", "apps", "backend", ".env"), "utf8");
const env = Object.fromEntries(
  envText
    .split(/\r?\n/)
    .filter((line) => line.includes("=") && !line.trim().startsWith("#"))
    .map((line) => [line.slice(0, line.indexOf("=")).trim(), line.slice(line.indexOf("=") + 1).trim()]),
);

const apiKey = env.GROQ_API_KEY;
const model = env.GROQ_MODEL || "meta-llama/llama-4-scout-17b-16e-instruct";
if (!apiKey) throw new Error("GROQ_API_KEY not set");

const png = readFileSync(join(here, "synthetic-app.png")).toString("base64");

const question = process.argv[2] ?? "Where is the Tools menu?";

const prompt = `You are the visual target detector for a Windows AI assistant.

Your task is to identify the exact visible UI element requested by the user in the supplied screenshot.

You are NOT a general chatbot.
You must return ONLY the required JSON object.

Verification step before answering:
1. In sceneReading, list LEFT TO RIGHT the exact text of every visible label in the row (or region) that contains the requested element.
2. Match the user's requested element against that list by its TEXT.
3. Count positions in the list so you target the matching element, never a neighbor.
If the text does not match any label, the element is not visible.

Return ONLY valid JSON.`;

const completion = await fetch("https://api.groq.com/openai/v1/chat/completions", {
  method: "POST",
  headers: { Authorization: `Bearer ${apiKey}`, "Content-Type": "application/json" },
  body: JSON.stringify({
    model,
    temperature: 0,
    max_tokens: 600,
    messages: [
      { role: "system", content: prompt },
      {
        role: "user",
        content: [
          { type: "text", text: `Where is: ${question}` },
          { type: "image_url", image_url: { url: `data:image/png;base64,${png}` } },
        ],
      },
    ],
    response_format: {
      type: "json_schema",
      json_schema: {
        name: "visual_target",
        strict: true,
        schema: {
          type: "object",
          additionalProperties: false,
          required: ["instruction", "sceneReading", "target", "label", "confidence"],
          properties: {
            instruction: { type: "string" },
            sceneReading: { type: "array", items: { type: "string" } },
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
      },
    },
  }),
});

const body = await completion.json();
console.log(JSON.stringify(body, null, 2));
