/**
 * V1.5.3 E2E — real end-to-end test of POST /api/ai/visual-teacher against the
 * live backend (npm run dev) using the synthetic screenshot with a known layout.
 *
 * Scenarios (spec STEPS 9-12):
 *   "Where is the File menu?"   → AI cursor target must land on File
 *   "Where is the View menu?"   → View
 *   "Where is the Tools menu?"  → Tools
 *   "Where is the Window menu?" → Window
 *   "Where is the Close button?" → the red Close button (not the whole window)
 *   "Where is the Photoshop button?" → target must be null, no invented coords
 *
 * Usage: node tests/e2e/run-visual-teacher-e2e.mjs [baseUrl]
 */
import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const baseUrl = process.argv[2] ?? "http://localhost:3000";

const png = readFileSync(join(here, "synthetic-app.png"));
const base64 = png.toString("base64");
const rawPositions = readFileSync(join(here, "menu-positions.json"), "utf8");
const positions = JSON.parse(rawPositions.replace(/^\uFEFF/, "")); // strip PowerShell BOM

const SCREEN_WIDTH = 1920;
const SCREEN_HEIGHT = 1080;
const TOLERANCE_PX = 60; // bounding-box centers must be within this distance

function center(rect) {
  return { x: rect.x + rect.width / 2, y: rect.y + rect.height / 2 };
}

function distance(a, b) {
  return Math.hypot(a.x - b.x, a.y - b.y);
}

async function askVisualTeacher(question) {
  const response = await fetch(`${baseUrl}/api/ai/visual-teacher`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({
      question,
      image: `data:image/png;base64,${base64}`,
      screenWidth: SCREEN_WIDTH,
      screenHeight: SCREEN_HEIGHT,
    }),
  });

  const status = response.status;
  const body = await response.json();
  return { status, body };
}

let failures = 0;

function assert(condition, message) {
  if (condition) {
    console.log(`  PASS  ${message}`);
  } else {
    failures += 1;
    console.error(`  FAIL  ${message}`);
  }
}

const scenarios = [
  { question: "Where is the File menu?", expected: "File" },
  { question: "Where is the View menu?", expected: "View" },
  { question: "Where is the Tools menu?", expected: "Tools" },
  { question: "Where is the Window menu?", expected: "Window" },
  { question: "Where is the Close button?", expected: "Close" },
  { question: "Where is the Photoshop button?", expected: null },
];

for (const { question, expected } of scenarios) {
  console.log(`\n[Scenario] "${question}" → expected: ${expected ?? "target=null"}`);
  let result;
  try {
    result = await askVisualTeacher(question);
  } catch (error) {
    failures += 1;
    console.error(`  FAIL  request failed: ${error.message}`);
    continue;
  }

  if (result.status !== 200) {
    failures += 1;
    console.error(`  FAIL  HTTP ${result.status}: ${JSON.stringify(result.body).slice(0, 300)}`);
    continue;
  }

  const { success, instruction, target, normalizedTarget, label, confidence } = result.body;

  assert(success === true, "success is true");
  assert(typeof instruction === "string" && instruction.length > 0 && instruction.length <= 300,
    `instruction is short text (got: "${instruction?.slice(0, 80)}")`);
  assert(!/<[a-z][\s\S]*>/i.test(instruction), "instruction contains no HTML/Markdown markup");

  if (expected === null) {
    assert(target === null, "target is null (no invented coordinates)");
    assert(normalizedTarget === undefined || normalizedTarget === null, "normalizedTarget is absent/null");
    assert(confidence === 0, `confidence is 0 (got ${confidence})`);
    continue;
  }

  const expectedPx = positions[expected];
  assert(target != null, "target is present");
  if (target == null) continue;

  // target must be in SCREEN PIXELS (backend converts from the normalized grid)
  assert(
    target.x >= 0 && target.y >= 0 &&
    target.x + target.width <= SCREEN_WIDTH && target.y + target.height <= SCREEN_HEIGHT,
    "target is within screenshot pixel bounds",
  );

  const gotCenter = center(target);
  const wantCenter = center(expectedPx);
  const dist = distance(gotCenter, wantCenter);
  assert(dist <= TOLERANCE_PX,
    `target center (${gotCenter.x.toFixed(0)}, ${gotCenter.y.toFixed(0)}) is on "${expected}" ` +
    `(expected center ${wantCenter.x.toFixed(0)}, ${wantCenter.y.toFixed(0)}; distance ${dist.toFixed(0)}px ≤ ${TOLERANCE_PX}px)`);

  // Target must be the element, not the whole window.
  assert(target.width <= expectedPx.width + TOLERANCE_PX,
    `target width ${target.width}px ≈ "${expected}" width ${expectedPx.width}px (not the whole window)`);

  assert(typeof confidence === "number" && confidence >= 0.5,
    `confidence ${confidence} ≥ 0.5`);
  assert(typeof label === "string" && label.length > 0, `label present ("${label}")`);

  if (normalizedTarget) {
    const normCenter = center(normalizedTarget);
    const normExpected = {
      x: (wantCenter.x / SCREEN_WIDTH) * 1000,
      y: (wantCenter.y / SCREEN_HEIGHT) * 1000,
    };
    const normDist = distance(normCenter, normExpected) * (SCREEN_WIDTH / 1000);
    assert(normDist <= TOLERANCE_PX, "normalizedTarget maps back onto the same element");
  }
}

console.log(`\n${failures === 0 ? "ALL E2E SCENARIOS PASSED" : `${failures} E2E ASSERTION(S) FAILED`}`);
process.exit(failures === 0 ? 0 : 1);
