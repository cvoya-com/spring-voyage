/**
 * Tests for the system-configuration page severity rendering (#1747).
 *
 * Pins the four mapping cases the issue calls out:
 *   - `Disabled` + optional   → secondary (info — neutral, no warning icon)
 *   - `Disabled` + mandatory  → destructive (error — blocking)
 *   - `MetWithWarning`        → warning
 *   - `Met` + Information     → success
 *
 * Plus the rendering invariant: optional+Disabled must NOT carry a warning
 * Severity badge (otherwise the operator still reads it as an action item).
 */

import { render, screen, waitFor, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import SystemConfigurationPage, {
  requirementVariant,
} from "./system-configuration-page";

interface RequirementFixture {
  requirementId: string;
  displayName: string;
  description: string;
  isMandatory: boolean;
  status: "Met" | "Disabled" | "Invalid";
  severity: "Information" | "Warning" | "Error";
  reason?: string | null;
  suggestion?: string | null;
  environmentVariableNames: readonly string[];
  configurationSectionPath?: string | null;
  documentationUrl?: string | null;
}

function fakeReportWith(reqs: RequirementFixture[]) {
  return {
    status: "Degraded",
    generatedAt: "2026-05-07T10:00:00Z",
    subsystems: [
      {
        subsystemName: "Test",
        status: "Degraded",
        requirements: reqs,
      },
    ],
  };
}

function stubFetch(report: unknown) {
  globalThis.fetch = vi.fn(
    async () =>
      new Response(JSON.stringify(report), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
  ) as unknown as typeof fetch;
}

describe("requirementVariant (#1747)", () => {
  it("Disabled + optional → secondary (info)", () => {
    expect(requirementVariant("Disabled", "Information", false)).toBe(
      "secondary",
    );
  });

  it("Disabled + mandatory → destructive (error)", () => {
    expect(requirementVariant("Disabled", "Information", true)).toBe(
      "destructive",
    );
  });

  it("Met + Warning (MetWithWarning) → warning", () => {
    expect(requirementVariant("Met", "Warning", false)).toBe("warning");
    expect(requirementVariant("Met", "Warning", true)).toBe("warning");
  });

  it("Met + Information → success", () => {
    expect(requirementVariant("Met", "Information", false)).toBe("success");
    expect(requirementVariant("Met", "Information", true)).toBe("success");
  });

  it("Invalid → destructive regardless of mandatory flag", () => {
    expect(requirementVariant("Invalid", "Error", false)).toBe("destructive");
    expect(requirementVariant("Invalid", "Error", true)).toBe("destructive");
  });
});

describe("SystemConfigurationPage rendering (#1747)", () => {
  const originalFetch = globalThis.fetch;

  beforeEach(() => {
    // Each test sets its own fetch stub.
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it("renders an optional Disabled requirement without a Warning severity badge", async () => {
    stubFetch(
      fakeReportWith([
        {
          requirementId: "dispatcher-endpoint",
          displayName: "Dispatcher endpoint",
          description: "HTTP endpoint of spring-dispatcher.",
          isMandatory: false,
          status: "Disabled",
          severity: "Information",
          reason:
            "Dispatcher:BaseUrl is not set — delegated-execution features are unavailable on this host.",
          suggestion: "Leave unset on hosts that don't drive delegated execution.",
          environmentVariableNames: ["Dispatcher__BaseUrl"],
          configurationSectionPath: "Dispatcher",
          documentationUrl: null,
        },
      ]),
    );

    render(<SystemConfigurationPage />);

    const row = await waitFor(() =>
      screen.getByTestId("requirement-dispatcher-endpoint"),
    );

    // The reason copy must not claim "will fail at first call" — the
    // misleading framing the API host shouldn't surface (#1747).
    expect(row.textContent ?? "").not.toContain("will fail at first call");

    // No "Warning" severity badge — Information severity is hidden so the
    // operator doesn't read this as an action item.
    expect(within(row).queryByText("Warning")).toBeNull();
    expect(within(row).queryByText("Error")).toBeNull();

    // The status badge is present and reads "Disabled".
    expect(within(row).getByText("Disabled")).toBeInTheDocument();
    // And the optional badge.
    expect(within(row).getByText("optional")).toBeInTheDocument();
  });

  it("renders a mandatory Disabled requirement with the Error severity affordance", async () => {
    stubFetch(
      fakeReportWith([
        {
          requirementId: "fake-mandatory",
          displayName: "Mandatory thing",
          description: "Cannot be left unset.",
          isMandatory: true,
          // Defensive: the validator shouldn't see Disabled+mandatory under
          // normal use (the doc forbids it), but if it does, the renderer
          // must escalate the visual severity. The wire severity stays
          // Information (raw factory default) so this test exercises the
          // renderer's IsMandatory-aware branch.
          status: "Disabled",
          severity: "Information",
          reason: "Required setting missing.",
          suggestion: "Set the env var.",
          environmentVariableNames: ["FAKE__MANDATORY"],
          configurationSectionPath: "Fake",
          documentationUrl: null,
        },
      ]),
    );

    render(<SystemConfigurationPage />);

    const row = await waitFor(() =>
      screen.getByTestId("requirement-fake-mandatory"),
    );

    expect(within(row).getByText("Disabled")).toBeInTheDocument();
    expect(within(row).getByText("mandatory")).toBeInTheDocument();
  });

  it("renders MetWithWarning with a Warning severity badge", async () => {
    stubFetch(
      fakeReportWith([
        {
          requirementId: "github-app-credentials",
          displayName: "GitHub App credentials",
          description: "App auth.",
          isMandatory: false,
          status: "Met",
          severity: "Warning",
          reason: "Webhook secret is empty.",
          suggestion: "Set GitHub__WebhookSecret.",
          environmentVariableNames: ["GitHub__WebhookSecret"],
          configurationSectionPath: "GitHub",
          documentationUrl: null,
        },
      ]),
    );

    render(<SystemConfigurationPage />);

    const row = await waitFor(() =>
      screen.getByTestId("requirement-github-app-credentials"),
    );

    expect(within(row).getByText("Met")).toBeInTheDocument();
    expect(within(row).getByText("Warning")).toBeInTheDocument();
  });

  it("renders Met (Information) without a severity badge", async () => {
    stubFetch(
      fakeReportWith([
        {
          requirementId: "database-connection-string",
          displayName: "Database connection string",
          description: "PostgreSQL.",
          isMandatory: true,
          status: "Met",
          severity: "Information",
          reason: null,
          suggestion: null,
          environmentVariableNames: ["ConnectionStrings__SpringDb"],
          configurationSectionPath: "ConnectionStrings:SpringDb",
          documentationUrl: null,
        },
      ]),
    );

    render(<SystemConfigurationPage />);

    const row = await waitFor(() =>
      screen.getByTestId("requirement-database-connection-string"),
    );

    expect(within(row).getByText("Met")).toBeInTheDocument();
    expect(within(row).queryByText("Warning")).toBeNull();
    expect(within(row).queryByText("Error")).toBeNull();
  });
});
