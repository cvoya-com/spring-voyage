import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

// The post-bind tab calls the shared `api` client for repositories,
// collaborators, the install URL, and (unique to this surface) the
// per-unit GitHub config GET/PUT pair. Mock the module before importing
// the component so the module graph sees the stub.
vi.mock("@/lib/api/client", async () => {
  // The disabled-with-reason branch checks `err instanceof ApiError`,
  // so the mock needs to expose the real class shape — a bare object
  // would fail the instanceof check and the panel would never render.
  // Mirrors the wizard's stand-in so the two test files stay in lock
  // step (#1146).
  class MockApiError extends Error {
    constructor(
      public readonly status: number,
      public readonly statusText: string,
      public readonly body: unknown,
    ) {
      super(`API error ${status}: ${statusText}`);
      this.name = "ApiError";
    }
  }
  return {
    ApiError: MockApiError,
    api: {
      listGitHubRepositories: vi.fn(),
      listGitHubCollaborators: vi.fn(),
      getGitHubInstallUrl: vi.fn(),
      getUnitGitHubConfig: vi.fn(),
      putUnitGitHubConfig: vi.fn(),
    },
  };
});

import { api } from "@/lib/api/client";
import type { UnitGitHubConfigResponse } from "@/lib/api/types";
import { GitHubConnectorTab } from "@connector-github/connector-tab";

const mocked = vi.mocked(api);

const repoFixture = {
  installationId: 7777,
  repositoryId: 1,
  owner: "acme",
  repo: "platform",
  fullName: "acme/platform",
  private: false,
};

const DEFAULT_EVENTS = ["issues", "pull_request", "issue_comment"] as const;

function configFixture(
  overrides: Partial<UnitGitHubConfigResponse> = {},
): UnitGitHubConfigResponse {
  // Defaults match a freshly bound unit: repo + installation set, no
  // explicit reviewer, server-resolved events (eventsAreDefault: true).
  return {
    unitId: "u1",
    owner: "acme",
    repo: "platform",
    appInstallationId: 7777,
    events: [...DEFAULT_EVENTS],
    reviewer: null,
    eventsAreDefault: true,
    ...overrides,
  };
}

describe("GitHubConnectorTab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocked.listGitHubRepositories.mockResolvedValue([repoFixture]);
    mocked.listGitHubCollaborators.mockResolvedValue([]);
    mocked.getGitHubInstallUrl.mockResolvedValue({ url: "" });
  });

  // #1146 (parity with wizard #1127): when the persisted config carries
  // `eventsAreDefault: true`, the tab renders with the toggle checked
  // and the per-event row purely informational — checks reflect
  // DEFAULT_EVENTS, every event input is disabled.
  it("renders with Connector defaults checked when eventsAreDefault is true (#1146)", async () => {
    mocked.getUnitGitHubConfig.mockResolvedValue(configFixture());

    await act(async () => {
      render(<GitHubConnectorTab unitId="u1" />);
    });

    const toggle = (await screen.findByTestId(
      "github-events-use-defaults",
    )) as HTMLInputElement;
    expect(toggle.checked).toBe(true);

    const issues = screen.getByLabelText("issues") as HTMLInputElement;
    const pr = screen.getByLabelText("pull_request") as HTMLInputElement;
    const ic = screen.getByLabelText("issue_comment") as HTMLInputElement;
    const push = screen.getByLabelText("push") as HTMLInputElement;
    const release = screen.getByLabelText("release") as HTMLInputElement;
    expect(issues.checked).toBe(true);
    expect(pr.checked).toBe(true);
    expect(ic.checked).toBe(true);
    expect(push.checked).toBe(false);
    expect(release.checked).toBe(false);
    [issues, pr, ic, push, release].forEach((input) =>
      expect(input).toBeDisabled(),
    );
  });

  // #1146: when the persisted config carries an explicit set
  // (`eventsAreDefault: false`), the toggle starts unchecked and the
  // per-event row is enabled and reflects the operator's selection.
  // This is the case the rejected client-side heuristic could not
  // distinguish — anti-regression for the contract decision recorded
  // on the PR.
  it("renders with Connector defaults UNchecked when eventsAreDefault is false (#1146)", async () => {
    mocked.getUnitGitHubConfig.mockResolvedValue(
      configFixture({
        events: ["push", "release"],
        eventsAreDefault: false,
      }),
    );

    await act(async () => {
      render(<GitHubConnectorTab unitId="u1" />);
    });

    const toggle = (await screen.findByTestId(
      "github-events-use-defaults",
    )) as HTMLInputElement;
    expect(toggle.checked).toBe(false);

    const push = screen.getByLabelText("push") as HTMLInputElement;
    const release = screen.getByLabelText("release") as HTMLInputElement;
    const issues = screen.getByLabelText("issues") as HTMLInputElement;
    expect(push.checked).toBe(true);
    expect(release.checked).toBe(true);
    expect(issues.checked).toBe(false);
    expect(push).not.toBeDisabled();
    expect(release).not.toBeDisabled();
    expect(issues).not.toBeDisabled();
  });

  // #1146 (matches wizard #1127): unchecking the toggle when the prior
  // state was "use defaults" pre-populates the per-event row with the
  // same DEFAULT_EVENTS the operator was already living with — they
  // never restart from an empty form.
  it("pre-populates the event row from DEFAULT_EVENTS when unchecked from defaults state (#1146)", async () => {
    mocked.getUnitGitHubConfig.mockResolvedValue(configFixture());

    await act(async () => {
      render(<GitHubConnectorTab unitId="u1" />);
    });

    const toggle = (await screen.findByTestId(
      "github-events-use-defaults",
    )) as HTMLInputElement;
    await act(async () => {
      fireEvent.click(toggle);
    });
    expect(toggle.checked).toBe(false);

    const issues = screen.getByLabelText("issues") as HTMLInputElement;
    const pr = screen.getByLabelText("pull_request") as HTMLInputElement;
    const ic = screen.getByLabelText("issue_comment") as HTMLInputElement;
    const push = screen.getByLabelText("push") as HTMLInputElement;
    const release = screen.getByLabelText("release") as HTMLInputElement;
    expect(issues).not.toBeDisabled();
    expect(issues.checked).toBe(true);
    expect(pr.checked).toBe(true);
    expect(ic.checked).toBe(true);
    expect(push.checked).toBe(false);
    expect(release.checked).toBe(false);
  });

  // #1146 (matches wizard #1127): when the prior state was an explicit
  // selection, unchecking the toggle preserves that selection rather
  // than reverting to DEFAULT_EVENTS.
  it("preserves the explicit selection when unchecked from explicit state (#1146)", async () => {
    mocked.getUnitGitHubConfig.mockResolvedValue(
      configFixture({
        events: ["push", "release"],
        eventsAreDefault: false,
      }),
    );

    await act(async () => {
      render(<GitHubConnectorTab unitId="u1" />);
    });

    const toggle = (await screen.findByTestId(
      "github-events-use-defaults",
    )) as HTMLInputElement;
    expect(toggle.checked).toBe(false);

    // Re-check then uncheck — the explicit selection must survive the
    // round-trip (we only seed from DEFAULT_EVENTS when the local
    // explicit list is empty, mirroring the wizard).
    await act(async () => {
      fireEvent.click(toggle);
    });
    expect(toggle.checked).toBe(true);
    await act(async () => {
      fireEvent.click(toggle);
    });
    expect(toggle.checked).toBe(false);

    const push = screen.getByLabelText("push") as HTMLInputElement;
    const release = screen.getByLabelText("release") as HTMLInputElement;
    const issues = screen.getByLabelText("issues") as HTMLInputElement;
    expect(push.checked).toBe(true);
    expect(release.checked).toBe(true);
    expect(issues.checked).toBe(false);
  });

  // #1146: re-checking the toggle disables the row again and the
  // checks revert to the informational DEFAULT_EVENTS view (the local
  // explicit list is preserved in component state but no longer drives
  // the visible checks while the toggle is on).
  it("re-checking Connector defaults disables the row and reverts the visible checks (#1146)", async () => {
    mocked.getUnitGitHubConfig.mockResolvedValue(
      configFixture({
        events: ["push", "release"],
        eventsAreDefault: false,
      }),
    );

    await act(async () => {
      render(<GitHubConnectorTab unitId="u1" />);
    });

    const toggle = (await screen.findByTestId(
      "github-events-use-defaults",
    )) as HTMLInputElement;
    expect(toggle.checked).toBe(false);

    await act(async () => {
      fireEvent.click(toggle);
    });
    expect(toggle.checked).toBe(true);

    const issues = screen.getByLabelText("issues") as HTMLInputElement;
    const push = screen.getByLabelText("push") as HTMLInputElement;
    const release = screen.getByLabelText("release") as HTMLInputElement;
    expect(issues).toBeDisabled();
    expect(push).toBeDisabled();
    expect(release).toBeDisabled();
    expect(issues.checked).toBe(true);
    expect(push.checked).toBe(false);
    expect(release.checked).toBe(false);
  });

  // #1146: saving with the toggle ON must omit `events` on the wire so
  // the server resolves the set itself (matches the wizard's wire
  // shape from #1127). The PUT response — which carries
  // `eventsAreDefault: true` — must round-trip back into the rendered
  // state.
  it("save with Connector defaults checked posts events: undefined and round-trips (#1146)", async () => {
    mocked.getUnitGitHubConfig.mockResolvedValue(configFixture());
    mocked.putUnitGitHubConfig.mockResolvedValue(configFixture());

    await act(async () => {
      render(<GitHubConnectorTab unitId="u1" />);
    });

    const saveButton = await screen.findByRole("button", { name: /save/i });
    await act(async () => {
      fireEvent.click(saveButton);
    });

    await waitFor(() =>
      expect(mocked.putUnitGitHubConfig).toHaveBeenCalled(),
    );

    const [calledUnit, calledBody] =
      mocked.putUnitGitHubConfig.mock.calls.at(-1)!;
    expect(calledUnit).toBe("u1");
    expect(calledBody.events).toBeUndefined();

    const toggle = (await screen.findByTestId(
      "github-events-use-defaults",
    )) as HTMLInputElement;
    expect(toggle.checked).toBe(true);
    const issues = screen.getByLabelText("issues") as HTMLInputElement;
    expect(issues).toBeDisabled();
  });

  // #1146: saving with the toggle OFF must POST the explicit array
  // verbatim. The PUT response — with `eventsAreDefault: false` — must
  // round-trip back into the rendered state so a refresh of the tab
  // continues to show the per-event row enabled and pre-populated.
  it("save with Connector defaults unchecked posts the chosen array and round-trips (#1146)", async () => {
    mocked.getUnitGitHubConfig.mockResolvedValue(configFixture());

    await act(async () => {
      render(<GitHubConnectorTab unitId="u1" />);
    });

    const toggle = (await screen.findByTestId(
      "github-events-use-defaults",
    )) as HTMLInputElement;
    await act(async () => {
      fireEvent.click(toggle);
    });
    expect(toggle.checked).toBe(false);

    const release = screen.getByLabelText("release") as HTMLInputElement;
    await act(async () => {
      fireEvent.click(release);
    });

    const expectedEvents = [...DEFAULT_EVENTS, "release"];
    mocked.putUnitGitHubConfig.mockResolvedValue(
      configFixture({
        events: expectedEvents,
        eventsAreDefault: false,
      }),
    );

    const saveButton = await screen.findByRole("button", { name: /save/i });
    await act(async () => {
      fireEvent.click(saveButton);
    });

    await waitFor(() =>
      expect(mocked.putUnitGitHubConfig).toHaveBeenCalled(),
    );

    const [, calledBody] = mocked.putUnitGitHubConfig.mock.calls.at(-1)!;
    expect(calledBody.events).toEqual(
      expect.arrayContaining([
        "issues",
        "pull_request",
        "issue_comment",
        "release",
      ]),
    );
    expect(calledBody.events).not.toContain("push");

    // After save, the box must remain unchecked and the row enabled —
    // the rendered state must match the persisted state. This is the
    // round-trip the rejected heuristic-on-client option could not
    // honour.
    const toggleAfter = (await screen.findByTestId(
      "github-events-use-defaults",
    )) as HTMLInputElement;
    expect(toggleAfter.checked).toBe(false);
    const issuesAfter = screen.getByLabelText("issues") as HTMLInputElement;
    expect(issuesAfter).not.toBeDisabled();
  });
});
