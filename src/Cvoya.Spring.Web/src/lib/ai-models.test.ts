import { describe, expect, it } from "vitest";

import {
  DEFAULT_HOSTING_MODE,
  DEFAULT_RUNTIME_ID,
  HOSTING_MODES,
  RUNTIMES,
  RUNTIME_LIST,
  getAllowedProviders,
  getFixedProvider,
  isRuntimeProviderFixed,
} from "./ai-models";

describe("RUNTIMES", () => {
  it("has claude-code as the default runtime", () => {
    expect(DEFAULT_RUNTIME_ID).toBe("claude-code");
  });

  it("includes all expected runtimes", () => {
    const ids = RUNTIME_LIST.map((r) => r.id);
    expect(ids).toContain("claude-code");
    expect(ids).toContain("codex");
    expect(ids).toContain("gemini");
    expect(ids).toContain("spring-voyage");
    expect(ids).toContain("custom");
  });

  it("declares fixed-provider runtimes correctly (ADR-0038 §1)", () => {
    expect(RUNTIMES["claude-code"].isProviderFixed).toBe(true);
    expect(RUNTIMES["claude-code"].fixedProvider).toBe("anthropic");
    expect(RUNTIMES.codex.isProviderFixed).toBe(true);
    expect(RUNTIMES.codex.fixedProvider).toBe("openai");
    expect(RUNTIMES.gemini.isProviderFixed).toBe(true);
    expect(RUNTIMES.gemini.fixedProvider).toBe("google");
  });

  it("declares spring-voyage as multi-provider", () => {
    expect(RUNTIMES["spring-voyage"].isProviderFixed).toBe(false);
    expect(RUNTIMES["spring-voyage"].fixedProvider).toBeNull();
    expect(RUNTIMES["spring-voyage"].allowedProviders).toEqual(
      expect.arrayContaining(["anthropic", "openai", "google", "ollama"]),
    );
  });
});

describe("hosting modes", () => {
  it("has persistent as the default hosting mode", () => {
    expect(DEFAULT_HOSTING_MODE).toBe("persistent");
  });

  it("includes ephemeral and persistent", () => {
    const ids = HOSTING_MODES.map((m) => m.id);
    expect(ids).toContain("ephemeral");
    expect(ids).toContain("persistent");
  });
});

describe("isRuntimeProviderFixed", () => {
  it("returns true for fixed-provider runtimes", () => {
    expect(isRuntimeProviderFixed("claude-code")).toBe(true);
    expect(isRuntimeProviderFixed("codex")).toBe(true);
    expect(isRuntimeProviderFixed("gemini")).toBe(true);
  });

  it("returns false for multi-provider / unknown runtimes", () => {
    expect(isRuntimeProviderFixed("spring-voyage")).toBe(false);
    expect(isRuntimeProviderFixed("custom")).toBe(false);
    expect(isRuntimeProviderFixed(null)).toBe(false);
    expect(isRuntimeProviderFixed("nonsense")).toBe(false);
  });
});

describe("getFixedProvider", () => {
  it("resolves fixed-provider runtimes to their provider id", () => {
    expect(getFixedProvider("claude-code")).toBe("anthropic");
    expect(getFixedProvider("codex")).toBe("openai");
    expect(getFixedProvider("gemini")).toBe("google");
  });

  it("returns null for multi-provider / unknown runtimes", () => {
    expect(getFixedProvider("spring-voyage")).toBeNull();
    expect(getFixedProvider("custom")).toBeNull();
    expect(getFixedProvider(null)).toBeNull();
  });
});

describe("getAllowedProviders", () => {
  it("returns the singleton allow-list for fixed-provider runtimes", () => {
    expect(getAllowedProviders("claude-code")).toEqual(["anthropic"]);
    expect(getAllowedProviders("codex")).toEqual(["openai"]);
    expect(getAllowedProviders("gemini")).toEqual(["google"]);
  });

  it("returns the broad allow-list for spring-voyage", () => {
    const providers = getAllowedProviders("spring-voyage");
    expect(providers).toEqual(
      expect.arrayContaining(["anthropic", "openai", "google", "ollama"]),
    );
  });

  it("returns null for unknown runtime ids", () => {
    expect(getAllowedProviders(null)).toBeNull();
    expect(getAllowedProviders("nonsense")).toBeNull();
  });
});
