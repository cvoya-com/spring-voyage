import { describe, expect, it } from "vitest";

import {
  AI_PROVIDERS,
  DEFAULT_EXECUTION_TOOL,
  DEFAULT_HOSTING_MODE,
  DEFAULT_MODEL,
  DEFAULT_PROVIDER_ID,
  EXECUTION_TOOLS,
  HOSTING_MODES,
  getProvider,
} from "./ai-models";

describe("ai-models catalog", () => {
  it("exposes at least one provider", () => {
    expect(AI_PROVIDERS.length).toBeGreaterThan(0);
  });

  it("ships at least one model per provider", () => {
    for (const provider of AI_PROVIDERS) {
      expect(provider.models.length).toBeGreaterThan(0);
    }
  });

  it("uses the first provider as the default", () => {
    expect(DEFAULT_PROVIDER_ID).toBe(AI_PROVIDERS[0].id);
  });

  it("uses the first provider's first model as the default", () => {
    expect(DEFAULT_MODEL).toBe(AI_PROVIDERS[0].models[0]);
  });

  it("keeps the platform-wide claude-sonnet-4 default in sync", () => {
    // Bug #258: changing this string requires a matching change to
    // Cvoya.Spring.Dapr/Execution/AiProviderOptions.cs. Catch drift early.
    expect(DEFAULT_MODEL).toBe("claude-sonnet-4-20250514");
  });

  it("includes claude, openai, google, and ollama providers", () => {
    const ids = AI_PROVIDERS.map((p) => p.id);
    expect(ids).toContain("claude");
    expect(ids).toContain("openai");
    expect(ids).toContain("google");
    expect(ids).toContain("ollama");
  });

  it("ollama provider includes recommended models", () => {
    const ollama = getProvider("ollama");
    expect(ollama.models).toContain("qwen2.5:14b");
    expect(ollama.models).toContain("llama3.2:3b");
    expect(ollama.models).toContain("llama3.1:8b");
    expect(ollama.models).toContain("mistral:7b");
    expect(ollama.models).toContain("deepseek-coder-v2:16b");
  });

  it("openai provider includes gpt-4o models", () => {
    const openai = getProvider("openai");
    expect(openai.models).toContain("gpt-4o");
    expect(openai.models).toContain("gpt-4o-mini");
    expect(openai.models).toContain("o3-mini");
  });

  it("google provider includes gemini-2.5 models", () => {
    const google = getProvider("google");
    expect(google.models).toContain("gemini-2.5-pro");
    expect(google.models).toContain("gemini-2.5-flash");
  });
});

describe("execution tools", () => {
  it("has claude-code as the default execution tool", () => {
    expect(DEFAULT_EXECUTION_TOOL).toBe("claude-code");
  });

  it("includes all expected tools", () => {
    const ids = EXECUTION_TOOLS.map((t) => t.id);
    expect(ids).toContain("claude-code");
    expect(ids).toContain("codex");
    expect(ids).toContain("gemini");
    expect(ids).toContain("dapr-agent");
    expect(ids).toContain("custom");
  });
});

describe("hosting modes", () => {
  it("has ephemeral as the default hosting mode", () => {
    expect(DEFAULT_HOSTING_MODE).toBe("ephemeral");
  });

  it("includes ephemeral and persistent", () => {
    const ids = HOSTING_MODES.map((m) => m.id);
    expect(ids).toContain("ephemeral");
    expect(ids).toContain("persistent");
  });
});

describe("getProvider", () => {
  it("returns the matching provider when the id is known", () => {
    const provider = getProvider("claude");
    expect(provider.id).toBe("claude");
  });

  it("falls back to the first provider when the id is unknown", () => {
    const provider = getProvider("not-a-provider");
    expect(provider.id).toBe(AI_PROVIDERS[0].id);
  });

  it("falls back to the first provider on empty string", () => {
    const provider = getProvider("");
    expect(provider.id).toBe(AI_PROVIDERS[0].id);
  });
});
