import { describe, expect, it } from "vitest";

import {
  AI_PROVIDERS,
  DEFAULT_MODEL,
  DEFAULT_PROVIDER_ID,
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

  it("includes claude, codex, and gemini providers", () => {
    const ids = AI_PROVIDERS.map((p) => p.id);
    expect(ids).toContain("claude");
    expect(ids).toContain("codex");
    expect(ids).toContain("gemini");
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
