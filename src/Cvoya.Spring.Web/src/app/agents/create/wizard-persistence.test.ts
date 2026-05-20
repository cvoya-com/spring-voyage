import { beforeEach, describe, expect, it } from "vitest";

import {
  AGENT_WIZARD_SESSION_KEY,
  AGENT_WIZARD_STATE_SCHEMA_VERSION,
  clearAgentWizardSnapshot,
  loadAgentWizardSnapshot,
  saveAgentWizardSnapshot,
  type AgentWizardSnapshot,
} from "./wizard-persistence";

function makeSnapshot(
  overrides: Partial<AgentWizardSnapshot> = {},
): AgentWizardSnapshot {
  return {
    schemaVersion: AGENT_WIZARD_STATE_SCHEMA_VERSION,
    source: "scratch",
    displayName: "Ada Lovelace",
    description: "Reviews backend changes",
    role: "reviewer",
    runtime: "claude-code",
    modelProviderId: "anthropic",
    modelId: "claude-sonnet-4-6",
    hosting: "ephemeral",
    image: "ghcr.io/cvoya-com/agent:latest",
    ...overrides,
  };
}

describe("agent wizard persistence", () => {
  beforeEach(() => {
    sessionStorage.clear();
  });

  it("round-trips a valid snapshot through save + load", () => {
    const snapshot = makeSnapshot();

    saveAgentWizardSnapshot(snapshot);

    expect(sessionStorage.getItem(AGENT_WIZARD_SESSION_KEY)).not.toBeNull();
    expect(loadAgentWizardSnapshot()).toEqual(snapshot);
  });

  it("returns null for a snapshot whose schema version doesn't match", () => {
    sessionStorage.setItem(
      AGENT_WIZARD_SESSION_KEY,
      JSON.stringify({
        ...makeSnapshot(),
        schemaVersion: AGENT_WIZARD_STATE_SCHEMA_VERSION + 99,
      }),
    );

    expect(loadAgentWizardSnapshot()).toBeNull();
  });

  it("clearAgentWizardSnapshot removes the snapshot slot", () => {
    saveAgentWizardSnapshot(makeSnapshot());

    clearAgentWizardSnapshot();

    expect(sessionStorage.getItem(AGENT_WIZARD_SESSION_KEY)).toBeNull();
    expect(loadAgentWizardSnapshot()).toBeNull();
  });
});
