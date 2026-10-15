/**
 * Runs the REAL backend visual-target request 3 times against the synthetic
 * screenshot, printing the raw model output each time to separate prompt
 * effects from run-to-run variance.
 * Usage: npx tsx scripts/diagnose-vision.ts "Where is the Tools menu?"
 */
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import OpenAI from "openai";

import { VISUAL_TARGET_SCHEMA, VISUAL_TEACHER_PROMPT } from "../src/services/ai/visualGuidance.js";

const here = dirname(fileURLToPath(import.meta.url));
const envText = readFileSync(join(here, "..", ".env"), "utf8");
const env = Object.fromEntries(
  envText
    .split(/\r?\n/)
    .filter((line) => line.includes("=") && !line.trim().startsWith("#"))
    .map((line) => [line.slice(0, line.indexOf("=")).trim(), line.slice(line.indexOf("=") + 1).trim()]),
);

const apiKey = env.GROQ_API_KEY;
const model = env.GROQ_MODEL || "qwen/qwen3.8-27b";
if (!apiKey) throw new Error("GROQ_API_KEY not set");

const png = readFileSync(join(here, "..", "..", "..", "tests", "e2e", "synthetic-app.png")).toString("base64");
const question = process.argv[2] ?? "Where is the Tools menu?";

const client = new OpenAI({ apiKey, baseURL: "https://api.groq.com/openai/v1", maxRetries: 2 });

const messages = [
  { role: "system" as const, content: VISUAL_TEACHER_PROMPT },
  {
    role: "user" as const,
    content: [
      { type: "text" as const, text: `Where is: ${question}` },
      { type: "image_url" as const, image_url: { url: `data:image/png;base64,${png}` } },
    ],
  },
];

for (let attempt = 1; attempt <= 3; attempt++) {
  const completion = await client.chat.completions.create({
    model,
    temperature: 0,
    max_tokens: 600,
    messages,
    response_format: { type: "json_schema", json_schema: VISUAL_TARGET_SCHEMA },
  });
  console.log(`--- attempt ${attempt} ---`);
  console.log(completion.choices[0]?.message?.content);
}
