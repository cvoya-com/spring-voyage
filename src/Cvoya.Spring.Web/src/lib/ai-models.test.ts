import { describe, expect, it } from "vitest";

import {
  DEFAULT_EXECUTION_TOOL,
  DEFAULT_HOSTING_MODE,
  EXECUTION_TOOLS,
  HOSTING_MODES,
  getToolRuntimeId,
  getToolWireProvider,
  getRuntimeSecretName,
} from "./ai-models";

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

describe("getToolRuntimeId", () => {
  it("maps fixed-provider tools to their canonical runtime id", () => {
    expect(getToolRuntimeId("claude-code")).toBe("claude");
    expect(getToolRuntimeId("codex")).toBe("openai");
    expect(getToolRuntimeId("gemini")).toBe("google");
  });

  it("returns null for tools that don't imply a runtime", () => {
    expect(getToolRuntimeId("dapr-agent")).toBeNull();
    expect(getToolRuntimeId("custom")).toBeNull();
  });
});

describe("getToolWireProvider", () => {
  it("carries a fixed provider for Claude / Codex / Gemini", () => {
    expect(getToolWireProvider("claude-code", null)).toBe("claude");
    expect(getToolWireProvider("codex", null)).toBe("openai");
    expect(getToolWireProvider("gemini", null)).toBe("google");
  });

  it("passes the dapr-agent runtime id through verbatim", () => {
    expect(getToolWireProvider("dapr-agent", "ollama")).toBe("ollama");
    expect(getToolWireProvider("dapr-agent", null)).toBeUndefined();
  });

  it("returns undefined for custom tools", () => {
    expect(getToolWireProvider("custom", null)).toBeUndefined();
  });
});

describe("getRuntimeSecretName", () => {
  it("returns the canonical secret name for each provider alias", () => {
    expect(getRuntimeSecretName("claude")).toBe("anthropic-api-key");
    expect(getRuntimeSecretName("anthropic")).toBe("anthropic-api-key");
    expect(getRuntimeSecretName("openai")).toBe("openai-api-key");
    expect(getRuntimeSecretName("google")).toBe("google-api-key");
    expect(getRuntimeSecretName("gemini")).toBe("google-api-key");
  });

  it("returns null for providers without a known secret", () => {
    expect(getRuntimeSecretName("ollama")).toBeNull();
    expect(getRuntimeSecretName("unknown")).toBeNull();
  });
});
