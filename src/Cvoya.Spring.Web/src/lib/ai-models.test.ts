import { describe, expect, it } from "vitest";

import {
  DEFAULT_EXECUTION_TOOL,
  DEFAULT_HOSTING_MODE,
  EXECUTION_TOOLS,
  HOSTING_MODES,
  getToolRuntimeId,
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
    expect(ids).toContain("spring-voyage");
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
    expect(getToolRuntimeId("spring-voyage")).toBeNull();
    expect(getToolRuntimeId("custom")).toBeNull();
  });
});

// `getToolWireProvider` and `getRuntimeSecretName` were retired in
// ADR-0038 (PR-1b). Their replacement — per-edge resolution against
// `runtime-catalog.yaml` — lands in PR-3 (#1761).
