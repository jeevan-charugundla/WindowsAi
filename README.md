<h1 align="center">🪟 Windows AI Companion</h1>

<p align="center">
  A privacy-first desktop AI assistant for Windows that can <b>see your screen</b> and <b>teach you where to click</b>.<br/>
  <sub>WPF (C#/.NET 8) frontend · Node.js + TypeScript backend · Groq-hosted AI</sub>
</p>

<p align="center">
  <img alt="platform" src="https://img.shields.io/badge/platform-Windows-blue">
  <img alt="frontend" src="https://img.shields.io/badge/frontend-WPF%20%2F%20.NET%208-5C2D91">
  <img alt="backend" src="https://img.shields.io/badge/backend-Express%20%2B%20TypeScript-3178C6">
  <img alt="AI" src="https://img.shields.io/badge/AI-Groq%20%2B%20Whisper-orange">
</p>

---

## ✨ What is this?

**Windows AI Companion** is a chat application with *eyes*. You talk to an AI assistant like any chat app, but you can also:

- **📸 Analyze Screen** — your screen is captured and the AI answers questions about what's on it ("What's the error in this window?").
- **🎯 Guide Me** — visual *target detection*, not just chat. The AI returns the exact bounding box of the element you asked about ("Where is the File menu?") and a **teaching overlay** appears on your screen, highlighting the real UI element where it lives.
- **🎙️ Voice input** — push-to-talk transcription so you can ask hands-free.

It deliberately **guides instead of controls**: the app never moves your mouse or clicks anything for you — it shows *you* where to act.

## 🧭 Guide Me — how it works

Guide Me is intentionally NOT a chat feature: AI prose can never leak into the conversation. Each request runs a strict pipeline:

1. **Capture** — the desktop app screenshots the screen, saving real screen dimensions.
2. **Visual teacher** — the backend calls Groq vision with a strict system prompt and a JSON schema. The model must first *read* the relevant screen region left-to-right, giving each label it sees its own bounding box (**set-of-marks grounding**), before locating the requested element.
3. **Deterministic resolution** — the backend matches the user's question keywords against the model's own reading boxes, so the box reported is guaranteed to belong to the text the model actually read — eliminating look-alike coordinate errors.
4. **Fail-closed validation** — every response is schema-validated: coordinates must be integers on a 0–1000 normalized grid, labels ≤ 100 chars, confidence in [0, 1]. Anything out of bounds, low-confidence, or unparseable returns the canonical "not found" response. Invalid AI output can never reach your screen.
5. **Overlay** — the desktop converts normalized coordinates to physical pixels (DPI-aware) and draws a resilient teaching overlay around the real element.

If the requested element genuinely isn't visible, the AI returns `target: null` with a short honest instruction — it is never allowed to guess a location.


## 🏗️ Architecture

```
WindowsAICompanion/
├── apps/
│   ├── desktop/       # WPF (C#, .NET 8) — chat UI, screen capture, voice input, overlay
│   │   ├── MainWindow.xaml(.cs)          # chat + tool buttons + Guide Me flow
│   │   ├── VisualOverlayWindow(.cs)      # teaching overlay (always-on-top, resilient)
│   │   ├── VisualTargetMapper.cs         # pure normalized→pixel mapping (unit-tested)
│   │   ├── BackendApiClient.cs           # typed client for the AI backend
│   │   └── VisualGuidance.cs             # structured response model
│   └── backend/       # Node.js + TypeScript (Express)
│       └── src/
│           ├── server.ts                 # REST endpoints, request validation
│           └── services/ai/
│               ├── aiService.ts          # AI service facade
│               ├── groqAIService.ts      # Groq chat/vision implementation
│               ├── visualGuidance.ts     # prompt, schema, set-of-marks, validation
│               └── visualGuidance.test.ts
├── tests/
│   ├── WindowsAICompanion.Desktop.Tests/ # xUnit — deserialization, mapping, wiring
│   └── e2e/                              # real-E2E against live Groq vision
└── docs/                                 # per-version design specs
```

## 📡 API

| Endpoint | Purpose |
|----------|---------|
| `GET /health` | Liveness probe |
| `POST /api/ai/chat` | Streaming chat with the assistant |
| `POST /api/ai/analyze-screen` | Screen + question → AI analysis |
| `POST /api/ai/visual-teacher` | Screen + question → **structured** target `{ instruction, target, label, confidence }` (validated, fail-closed) |
| `POST /api/ai/transcribe` | Audio upload → Whisper transcription |

## 🚀 Getting started

### Prerequisites

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Node.js 18+](https://nodejs.org/)
- A free [Groq API key](https://console.groq.com/keys)

### 1. Backend

```bash
cd apps/backend
npm install
cp .env.example .env        # then set GROQ_API_KEY=your-key
npm run dev                 # http://localhost:3000
```

### 2. Desktop app

```bash
dotnet build WindowsAICompanion.sln
dotnet run --project apps/desktop
```

## 🧪 Tests

```bash
# Backend unit tests (node:test)
cd apps/backend && npm test

# Desktop unit tests (xUnit)
dotnet test WindowsAICompanion.sln
```

Tests cover: response parsing/validation/fail-closed semantics, normalized↔pixel target mapping, backend API deserialization, and source-level guard tests (Guide Me may only use the dedicated visual-teacher endpoint and structured model — never chat).

## 📜 Version history

- **v1.5** — Visual Teacher: screen questions without computer control
- **v1.5.2** — Teaching Cursor overlay
- **v1.5.3** — Structured Guide Me targets: strict JSON schema, set-of-marks grounding, fail-closed validation, message-only flow
- **v1.6** — Voice input (Whisper, push-to-talk)
- **v1.6.1** — Markdown rendering in chat

See [`docs/`](docs/) for the design spec of each version.

## 🔐 Privacy & security

- API keys live only in a local `.env` (git-ignored) and are never sent to the desktop app.
- Screen screenshots are sent only for the requests you trigger; nothing is stored by the app.

## 📄 License

MIT
