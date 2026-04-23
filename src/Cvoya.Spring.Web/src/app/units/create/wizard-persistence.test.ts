import { beforeEach, describe, expect, it } from "vitest";

import {
  WIZARD_STATE_SCHEMA_VERSION,
  clearWizardRun,
  clearWizardSnapshot,
  generateWizardRunId,
  loadOrInitWizardRunId,
  loadWizardSnapshot,
  saveWizardSnapshot,
  validateSnapshot,
  wizardSessionKey,
  type WizardFormSnapshot,
  type WizardSnapshot,
} from "./wizard-persistence";

function makeForm(overrides: Partial<WizardFormSnapshot> = {}): WizardFormSnapshot {
  return {
    name: "acme",
    displayName: "Acme",
    description: "",
    provider: "claude",
    model: "claude-sonnet-4-6",
    color: "#6366f1",
    tool: "claude-code",
    hosting: "default",
    image: "",
    runtime: "",
    mode: "scratch",
    templateId: null,
    yamlText: "",
    yamlFileName: null,
    connectorSlug: null,
    connectorTypeId: null,
    connectorConfig: null,
    parentUnitId: null,
    ...overrides,
  };
}

function makeSnapshot(
  overrides: Partial<WizardSnapshot> = {},
): WizardSnapshot {
  return {
    schemaVersion: WIZARD_STATE_SCHEMA_VERSION,
    currentStep: 3,
    form: makeForm(),
    ...overrides,
  };
}

describe("wizard-persistence", () => {
  beforeEach(() => {
    sessionStorage.clear();
  });

  it("round-trips a valid snapshot through save + load", () => {
    const runId = "run-1";
    const snapshot = makeSnapshot();
    saveWizardSnapshot(runId, snapshot);
    expect(loadWizardSnapshot(runId)).toEqual(snapshot);
  });

  it("returns null when no snapshot exists for the run id", () => {
    expect(loadWizardSnapshot("missing")).toBeNull();
  });

  // #1132 acceptance: an invalid snapshot must be discarded silently —
  // no crash, the wizard mounts at step 1 with empty fields. The
  // loader does NOT erase the malformed slot (a future schema-version
  // bump might be able to migrate it); it simply returns null.
  it("returns null for a snapshot whose schema version doesn't match", () => {
    const runId = "run-2";
    const stale: WizardSnapshot = {
      schemaVersion: (WIZARD_STATE_SCHEMA_VERSION + 99) as 1,
      currentStep: 4,
      form: makeForm(),
    };
    sessionStorage.setItem(wizardSessionKey(runId), JSON.stringify(stale));
    expect(loadWizardSnapshot(runId)).toBeNull();
  });

  it("returns null for a snapshot with a malformed currentStep", () => {
    const runId = "run-3";
    sessionStorage.setItem(
      wizardSessionKey(runId),
      JSON.stringify({
        schemaVersion: WIZARD_STATE_SCHEMA_VERSION,
        currentStep: 99,
        form: makeForm(),
      }),
    );
    expect(loadWizardSnapshot(runId)).toBeNull();
  });

  it("returns null for a snapshot whose form has the wrong shape", () => {
    const runId = "run-4";
    sessionStorage.setItem(
      wizardSessionKey(runId),
      JSON.stringify({
        schemaVersion: WIZARD_STATE_SCHEMA_VERSION,
        currentStep: 3,
        form: { name: 12345 },
      }),
    );
    expect(loadWizardSnapshot(runId)).toBeNull();
  });

  it("returns null for a non-JSON blob", () => {
    const runId = "run-5";
    sessionStorage.setItem(wizardSessionKey(runId), "not-json{");
    expect(loadWizardSnapshot(runId)).toBeNull();
  });

  it("validateSnapshot rejects mode values outside the canonical set", () => {
    expect(
      validateSnapshot({
        schemaVersion: WIZARD_STATE_SCHEMA_VERSION,
        currentStep: 3,
        form: makeForm({ mode: "rogue-mode" as never }),
      }),
    ).toBeNull();
  });

  it("validateSnapshot rejects array connectorConfig values", () => {
    expect(
      validateSnapshot({
        schemaVersion: WIZARD_STATE_SCHEMA_VERSION,
        currentStep: 3,
        form: makeForm({
          connectorConfig: ["unexpected"] as unknown as Record<string, unknown>,
        }),
      }),
    ).toBeNull();
  });

  it("clearWizardSnapshot removes the slot but leaves the run id pointer", () => {
    const runId = "run-6";
    sessionStorage.setItem("spring.wizard.unit-create.run-id", runId);
    saveWizardSnapshot(runId, makeSnapshot());
    clearWizardSnapshot(runId);
    expect(loadWizardSnapshot(runId)).toBeNull();
    expect(
      sessionStorage.getItem("spring.wizard.unit-create.run-id"),
    ).toBe(runId);
  });

  it("clearWizardRun removes both the snapshot and the run id pointer", () => {
    const runId = "run-7";
    sessionStorage.setItem("spring.wizard.unit-create.run-id", runId);
    saveWizardSnapshot(runId, makeSnapshot());
    clearWizardRun(runId);
    expect(loadWizardSnapshot(runId)).toBeNull();
    expect(
      sessionStorage.getItem("spring.wizard.unit-create.run-id"),
    ).toBeNull();
  });

  it("loadOrInitWizardRunId mints a fresh id and reuses it on subsequent calls", () => {
    const first = loadOrInitWizardRunId();
    expect(first).toMatch(/.+/);
    const second = loadOrInitWizardRunId();
    expect(second).toBe(first);
  });

  it("generateWizardRunId returns distinct ids across calls", () => {
    const a = generateWizardRunId();
    const b = generateWizardRunId();
    expect(a).not.toBe(b);
    expect(a.length).toBeGreaterThan(0);
  });

  // #1150: the wizard persists `parentUnitId` so a hard refresh of
  // /units/create?parent=foo doesn't lose the parent context. Two
  // cases matter:
  //   (a) round-trip a non-null parent id through save + load.
  //   (b) older blobs (pre-#1150) lack the field entirely — the
  //       loader must accept them and default the field to `null`
  //       (top-level), not discard the snapshot.
  it("round-trips a non-null parentUnitId (#1150)", () => {
    const runId = "run-parent-1";
    const snapshot = makeSnapshot({
      form: makeForm({ parentUnitId: "engineering" }),
    });
    saveWizardSnapshot(runId, snapshot);
    const loaded = loadWizardSnapshot(runId);
    expect(loaded).not.toBeNull();
    expect(loaded?.form.parentUnitId).toBe("engineering");
  });

  it("accepts pre-#1150 blobs that omit parentUnitId, defaulting to null", () => {
    const runId = "run-parent-2";
    // Hand-craft a snapshot that mimics a pre-#1150 sessionStorage
    // entry: every other field is present, but `parentUnitId` is
    // missing. The loader should treat it as a top-level wizard
    // (`null`) instead of throwing the snapshot away.
    const legacy = {
      schemaVersion: WIZARD_STATE_SCHEMA_VERSION,
      currentStep: 3,
      form: {
        name: "acme",
        displayName: "Acme",
        description: "",
        provider: "claude",
        model: "claude-sonnet-4-6",
        color: "#6366f1",
        tool: "claude-code",
        hosting: "default",
        image: "",
        runtime: "",
        mode: "scratch",
        templateId: null,
        yamlText: "",
        yamlFileName: null,
        connectorSlug: null,
        connectorTypeId: null,
        connectorConfig: null,
        // intentionally no `parentUnitId`
      },
    };
    sessionStorage.setItem(wizardSessionKey(runId), JSON.stringify(legacy));
    const loaded = loadWizardSnapshot(runId);
    expect(loaded).not.toBeNull();
    expect(loaded?.form.parentUnitId).toBeNull();
  });

  it("validateSnapshot rejects a non-string, non-null parentUnitId", () => {
    expect(
      validateSnapshot({
        schemaVersion: WIZARD_STATE_SCHEMA_VERSION,
        currentStep: 3,
        form: makeForm({ parentUnitId: 42 as unknown as string }),
      }),
    ).toBeNull();
  });
});
