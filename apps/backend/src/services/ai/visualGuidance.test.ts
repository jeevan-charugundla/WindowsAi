import assert from "node:assert/strict";
import { describe, it } from "node:test";

import {
  NO_TARGET_RESPONSE,
  NORMALIZED_COORDINATE_MAX,
  coerceTarget,
  extractJsonCandidate,
  isValidVisualGuidance,
  parseVisualGuidance,
} from "./visualGuidance.js";

import type { VisualGuidance } from "./aiService.js";

// ─────────────────────────────────────────────────────────────────────────────
// extractJsonCandidate
// ─────────────────────────────────────────────────────────────────────────────

describe("extractJsonCandidate", () => {
  it("returns plain JSON untouched", () => {
    const raw = `{"instruction":"Click the File menu."}`;
    assert.equal(extractJsonCandidate(raw), raw);
  });

  it("strips surrounding prose", () => {
    const raw = `Based on the image provided, here is the answer:\n{"instruction":"Click the File menu."}\nLet me know!`;
    assert.equal(extractJsonCandidate(raw), `{"instruction":"Click the File menu."}`);
  });

  it("strips markdown code fences", () => {
    const raw = "```json\n{\"instruction\":\"Click\"}\n```";
    assert.equal(extractJsonCandidate(raw), `{"instruction":"Click"}`);
  });

  it("strips reasoning-model think blocks", () => {
    const raw = `<think>Let me look at the image...</think>{"instruction":"Click"}`;
    assert.equal(extractJsonCandidate(raw), `{"instruction":"Click"}`);
  });

  it("returns empty string when no JSON object exists", () => {
    assert.equal(extractJsonCandidate("Based on the image provided, there is no Edit menu visible."), "");
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// coerceTarget
// ───────────────────────────────── coordinates ───────────────────────────────

describe("coerceTarget", () => {
  it("accepts integer coordinates", () => {
    assert.deepEqual(coerceTarget({ x: 35, y: 25, width: 55, height: 30 }), { x: 35, y: 25, width: 55, height: 30 });
  });

  it("floors float coordinates", () => {
    assert.deepEqual(coerceTarget({ x: 35.9, y: 24.2, width: 55.5, height: 30.8 }), { x: 35, y: 24, width: 55, height: 30 });
  });

  it("accepts numeric strings", () => {
    assert.deepEqual(coerceTarget({ x: "35", y: "25", width: "55", height: "30" }), { x: 35, y: 25, width: 55, height: 30 });
  });

  it("returns null for null/undefined/non-object", () => {
    assert.equal(coerceTarget(null), null);
    assert.equal(coerceTarget(undefined), null);
    assert.equal(coerceTarget("35,25"), null);
    assert.equal(coerceTarget(42), null);
  });

  it("returns null when any coordinate is missing or non-numeric", () => {
    assert.equal(coerceTarget({ x: 35, y: 25, width: 55 }), null);
    assert.equal(coerceTarget({ x: "abc", y: 25, width: 55, height: 30 }), null);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// parseVisualGuidance — CASE 1: valid target JSON
// ─────────────────────────────────────────────────────────────────────────────

describe("parseVisualGuidance", () => {
  const validRaw = JSON.stringify({
    instruction: "Click the File menu.",
    target: { x: 35, y: 25, width: 55, height: 30 },
    label: "File menu",
    confidence: 0.97,
  });

  it("CASE 1: parses a valid target response", () => {
    const result = parseVisualGuidance(validRaw);
    assert.deepEqual(result, {
      instruction: "Click the File menu.",
      target: { x: 35, y: 25, width: 55, height: 30 },
      label: "File menu",
      confidence: 0.97,
    });
  });

  it("CASE 2: target=null yields the canonical no-target response", () => {
    const raw = JSON.stringify({
      instruction: "I cannot find the requested element on the current screen.",
      target: null,
      label: null,
      confidence: 0,
    });
    assert.deepEqual(parseVisualGuidance(raw), { ...NO_TARGET_RESPONSE });
  });

  it("CASE 3: malformed JSON degrades to no-target", () => {
    assert.deepEqual(parseVisualGuidance("Based on the image provided, there is no Edit menu visible."), {
      ...NO_TARGET_RESPONSE,
    });
    assert.deepEqual(parseVisualGuidance("{not json at all"), { ...NO_TARGET_RESPONSE });
  });

  it("CASE 4: missing target key degrades to no-target", () => {
    const raw = JSON.stringify({ instruction: "Click the File menu.", label: "File", confidence: 0.9 });
    assert.deepEqual(parseVisualGuidance(raw), { ...NO_TARGET_RESPONSE });
  });

  it("CASE 5: out-of-range coordinates (x+width > 1000) are rejected", () => {
    const raw = JSON.stringify({
      instruction: "Click it",
      target: { x: 980, y: 10, width: 50, height: 20 },
      label: "Thing",
      confidence: 0.9,
    });
    const result = parseVisualGuidance(raw);
    assert.equal(result.target, null);
    assert.equal(result.confidence, 0);
  });

  it("CASE 6: negative coordinates are rejected", () => {
    const raw = JSON.stringify({
      instruction: "Click it",
      target: { x: -10, y: 5, width: 50, height: 20 },
      label: "Thing",
      confidence: 0.9,
    });
    assert.equal(parseVisualGuidance(raw).target, null);
  });

  it("CASE 7: confidence outside 0–1 is clamped to 0", () => {
    const high = JSON.stringify({
      instruction: "Click it",
      target: { x: 10, y: 10, width: 50, height: 20 },
      label: "Thing",
      confidence: 1.5,
    });
    assert.equal(parseVisualGuidance(high).confidence, 0);

    const low = JSON.stringify({
      instruction: "Click it",
      target: null,
      label: null,
      confidence: -0.2,
    });
    assert.equal(parseVisualGuidance(low).confidence, 0);
  });

  it("zero-size targets are rejected", () => {
    const raw = JSON.stringify({
      instruction: "Click it",
      target: { x: 10, y: 10, width: 0, height: 20 },
      label: "Thing",
      confidence: 0.9,
    });
    assert.equal(parseVisualGuidance(raw).target, null);
  });

  it("a target without a label is rejected", () => unsafeLabel());
  it("a target with an unsafe label is rejected", () => unsafeLabel("has <script>"));

  function unsafeLabel(label?: string) {
    const raw = JSON.stringify({
      instruction: "Click it",
      target: { x: 10, y: 10, width: 50, height: 20 },
      label,
      confidence: 0.9,
    });
    assert.equal(parseVisualGuidance(raw).target, null);
  }

  it("prose-wrapped JSON still parses (chat-style model output)", () => {
    const raw = `Based on the image provided, here is the target:\n${validRaw}\nI hope this helps!`;
    const result = parseVisualGuidance(extractJsonCandidate(raw));
    assert.deepEqual(result, {
      instruction: "Click the File menu.",
      target: { x: 35, y: 25, width: 55, height: 30 },
      label: "File menu",
      confidence: 0.97,
    });
  });

  it("respects the normalized grid constant", () => {
    assert.equal(NORMALIZED_COORDINATE_MAX, 1000);
  });
});

// ─────────────────────────────────────────────────────────────────────────────
// isValidVisualGuidance (backend STEP 6 gate)
// ─────────────────────────────────────────────────────────────────────────────

describe("isValidVisualGuidance", () => {
  const validGuidance: VisualGuidance = {
    instruction: "Click the File menu.",
    target: { x: 35, y: 25, width: 55, height: 30 },
    label: "File menu",
    confidence: 0.97,
  };

  it("accepts a valid targeted response", () => {
    assert.equal(isValidVisualGuidance(validGuidance), true);
  });

  it("accepts a valid no-target response", () => {
    assert.equal(isValidVisualGuidance({ ...NO_TARGET_RESPONSE }), true);
  });

  it("rejects out-of-range, negative, and zero-size targets", () => {
    for (const target of [
      { x: -1, y: 0, width: 10, height: 10 },
      { x: 0, y: -1, width: 10, height: 10 },
      { x: 0, y: 0, width: 0, height: 10 },
      { x: 0, y: 0, width: 10, height: 0 },
      { x: 990, y: 0, width: 20, height: 10 }, // x+width > 1000
      { x: 0, y: 990, width: 10, height: 20 }, // y+height > 1000
      { x: 0.5, y: 0, width: 10, height: 10 }, // non-integer
    ]) {
      assert.equal(isValidVisualGuidance({ ...validGuidance, target }), false);
    }
  });

  it("rejects missing label when a target exists", () => {
    assert.equal(isValidVisualGuidance({ ...validGuidance, label: null }), false);
  });

  it("rejects empty instruction or bad confidence", () => {
    assert.equal(isValidVisualGuidance({ ...validGuidance, instruction: "   " }), false);
    assert.equal(isValidVisualGuidance({ ...validGuidance, confidence: 1.2 }), false);
    assert.equal(isValidVisualGuidance({ ...validGuidance, confidence: -1 }), false);
  });
});
