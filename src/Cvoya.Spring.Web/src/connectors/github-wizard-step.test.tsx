import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

// The wizard-step component calls the shared `api` client for repositories,
// collaborators, and the install URL. Mock the module before importing the
// component so the module graph sees the stub.
vi.mock("@/lib/api/client", async () => {
  // The wizard's disabled-with-reason branch checks `err instanceof
  // ApiError`, so the mock needs to expose the real class shape — a bare
  // object would fail the instanceof check and the panel would never
  // render. We construct a thin stand-in here so tests can throw it.
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
      beginGitHubOAuthAuthorize: vi.fn(),
      getGitHubOAuthResult: vi.fn(),
      createTenantSecret: vi.fn(),
    },
  };
});

import { ApiError, api } from "@/lib/api/client";
import { expectNoAxeViolations } from "@/test/a11y";
import { GitHubConnectorWizardStep } from "@connector-github/connector-wizard-step";

const mocked = vi.mocked(api);

const repoFixture = {
  installationId: 7777,
  repositoryId: 1,
  owner: "acme",
  repo: "platform",
  fullName: "acme/platform",
  private: false,
};

const QUALIFIED_REPO_INPUT_LABEL = "Repository (qualified owner/repo)";
const APP_REPO_DROPDOWN_LABEL = "Repository (from GitHub App installations)";

describe("GitHubConnectorWizardStep", () => {
  beforeEach(() => {
    vi.resetAllMocks();
    window.sessionStorage.clear();
  });

  it("bubbles null until a repository is set (#1133)", async () => {
    mocked.listGitHubRepositories.mockResolvedValue([]);
    mocked.getGitHubInstallUrl.mockResolvedValue({ url: "" });
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    await waitFor(() => expect(onChange).toHaveBeenCalledWith(null));
  });

  // ADR-0047 §11: picking from the App dropdown fills the qualified
  // input AND auto-snaps the App installation id. The wizard bubbles
  // the qualified `repo` + `appInstallationId` on the App branch.
  it("emits a typed config payload when a repository is picked from the App dropdown (ADR-0047 §11)", async () => {
    mocked.listGitHubRepositories.mockResolvedValue([repoFixture]);
    mocked.listGitHubCollaborators.mockResolvedValue([]);
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    await waitFor(() =>
      expect(mocked.listGitHubRepositories).toHaveBeenCalled(),
    );

    await act(async () => {
      fireEvent.change(screen.getByLabelText(APP_REPO_DROPDOWN_LABEL), {
        target: { value: "acme/platform" },
      });
    });

    await waitFor(() => {
      const last = onChange.mock.calls.at(-1)?.[0];
      expect(last).toEqual(
        expect.objectContaining({
          repo: "acme/platform",
          appInstallationId: 7777,
        }),
      );
      // PAT secret name must be absent on the App branch (ADR-0047 §11
      // "exactly one of" gate is enforced server-side; the wizard
      // bubbles the correct shape).
      expect(last?.pat_secret_name).toBeUndefined();
    });
  });

  // ADR-0047 §11: rejects unqualified inputs at form-validation time
  // with a clear inline message; onChange stays null until the input
  // looks like `owner/repo`.
  it("rejects unqualified repo input with an inline message (ADR-0047 §11)", async () => {
    mocked.listGitHubRepositories.mockResolvedValue([]);
    mocked.getGitHubInstallUrl.mockResolvedValue({ url: "" });
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    await act(async () => {
      fireEvent.change(screen.getByLabelText(QUALIFIED_REPO_INPUT_LABEL), {
        target: { value: "platform" },
      });
    });

    expect(
      await screen.findByTestId("github-repo-validation"),
    ).toHaveTextContent(/owner\/repo/);

    await waitFor(() => {
      const last = onChange.mock.calls.at(-1)?.[0];
      expect(last).toBeNull();
    });
  });

  it("re-fetches collaborators when the repo changes and includes the picked reviewer (#1133)", async () => {
    mocked.listGitHubRepositories.mockResolvedValue([
      repoFixture,
      {
        ...repoFixture,
        repositoryId: 2,
        owner: "acme",
        repo: "ops",
        fullName: "acme/ops",
      },
    ]);
    mocked.listGitHubCollaborators.mockResolvedValue([
      { login: "alice", avatarUrl: null },
      { login: "bob", avatarUrl: null },
    ]);
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    await waitFor(() =>
      expect(mocked.listGitHubRepositories).toHaveBeenCalled(),
    );

    await act(async () => {
      fireEvent.change(screen.getByLabelText(APP_REPO_DROPDOWN_LABEL), {
        target: { value: "acme/platform" },
      });
    });

    await waitFor(() =>
      expect(mocked.listGitHubCollaborators).toHaveBeenCalledWith(
        7777,
        "acme",
        "platform",
      ),
    );

    await act(async () => {
      fireEvent.change(screen.getByLabelText("Default reviewer"), {
        target: { value: "alice" },
      });
    });

    await waitFor(() => {
      const last = onChange.mock.calls.at(-1)?.[0];
      expect(last).toEqual(
        expect.objectContaining({
          repo: "acme/platform",
          reviewer: "alice",
        }),
      );
    });

    // Switching the repository must clear the previously-chosen reviewer
    // (collaborators are repo-scoped) and re-fetch.
    await act(async () => {
      fireEvent.change(screen.getByLabelText(APP_REPO_DROPDOWN_LABEL), {
        target: { value: "acme/ops" },
      });
    });

    await waitFor(() =>
      expect(mocked.listGitHubCollaborators).toHaveBeenLastCalledWith(
        7777,
        "acme",
        "ops",
      ),
    );

    await waitFor(() => {
      const last = onChange.mock.calls.at(-1)?.[0];
      expect(last?.reviewer).toBeUndefined();
    });
  });

  // ADR-0047 §11 auth-choice: flipping to PAT clears `appInstallationId`
  // and gates the wizard on the secret name. Typing a PAT secret name
  // produces a payload with `pat_secret_name` set and no `app
  // installation id`.
  it("flips to the PAT branch and bubbles pat_secret_name (ADR-0047 §11)", async () => {
    mocked.listGitHubRepositories.mockResolvedValue([repoFixture]);
    mocked.listGitHubCollaborators.mockResolvedValue([]);
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    await waitFor(() =>
      expect(mocked.listGitHubRepositories).toHaveBeenCalled(),
    );

    // Pick a repo from the App dropdown first (so installationId is
    // populated), then flip to PAT.
    await act(async () => {
      fireEvent.change(screen.getByLabelText(APP_REPO_DROPDOWN_LABEL), {
        target: { value: "acme/platform" },
      });
    });

    await act(async () => {
      fireEvent.click(screen.getByTestId("github-auth-choice-pat"));
    });

    // While no token has been saved the wizard must NOT bubble a
    // payload — the PAT branch is incomplete.
    await waitFor(() => {
      const last = onChange.mock.calls.at(-1)?.[0];
      expect(last).toBeNull();
    });

    // Paste a token and "Use this token": the wizard stores it as a
    // tenant secret (App-free) and wires pat_secret_name to the name.
    mocked.createTenantSecret.mockResolvedValue({
      name: "ignored",
      scope: "Tenant",
      createdAt: "2026-01-01T00:00:00Z",
    });

    await act(async () => {
      fireEvent.change(screen.getByTestId("github-pat-token"), {
        target: { value: "ghp_exampletoken" },
      });
    });
    await act(async () => {
      fireEvent.click(screen.getByTestId("github-pat-save"));
    });

    await waitFor(() =>
      expect(mocked.createTenantSecret).toHaveBeenCalled(),
    );
    const savedBody = mocked.createTenantSecret.mock.calls.at(-1)![0];
    expect(savedBody.name).toMatch(/^binding\/[0-9a-f]{32}\/github\/pat$/);
    expect(savedBody.value).toBe("ghp_exampletoken");

    await waitFor(() => {
      const last = onChange.mock.calls.at(-1)?.[0];
      expect(last).toEqual(
        expect.objectContaining({
          repo: "acme/platform",
          pat_secret_name: savedBody.name,
        }),
      );
      // App branch fields must not leak across the auth-choice
      // boundary.
      expect(last?.appInstallationId).toBeUndefined();
    });
  });

  // The App-installations dropdown is gated by the auth choice, not by
  // repo count: a PAT binding can't enumerate arbitrary repos, so the
  // dropdown is meaningless on that branch. Mock a repo so the dropdown
  // WOULD render in App mode, then flip to PAT and assert it's gone
  // while the free-text qualified input stays.
  it("hides the App-installations dropdown on the PAT branch but keeps the qualified input", async () => {
    mocked.listGitHubRepositories.mockResolvedValue([repoFixture]);
    mocked.listGitHubCollaborators.mockResolvedValue([]);
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    await waitFor(() =>
      expect(mocked.listGitHubRepositories).toHaveBeenCalled(),
    );

    // App mode (the default): the dropdown renders because a repo is
    // visible.
    expect(
      screen.getByLabelText(APP_REPO_DROPDOWN_LABEL),
    ).toBeInTheDocument();

    await act(async () => {
      fireEvent.click(screen.getByTestId("github-auth-choice-pat"));
    });

    // PAT mode: the dropdown is gone even though a repo is visible, but
    // the free-text qualified owner/repo input remains as the only
    // repository control.
    expect(screen.queryByLabelText(APP_REPO_DROPDOWN_LABEL)).toBeNull();
    expect(
      screen.getByLabelText(QUALIFIED_REPO_INPUT_LABEL),
    ).toBeInTheDocument();
  });

  it("shows the install-app banner when no repositories are visible", async () => {
    mocked.listGitHubRepositories.mockResolvedValue([]);
    mocked.getGitHubInstallUrl.mockResolvedValue({ url: "" });
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    await waitFor(() =>
      expect(
        screen.getByText(/No GitHub repositories visible\./),
      ).toBeInTheDocument(),
    );
  });

  it("renders translated copy when listing repositories throws ProblemDetails", async () => {
    mocked.listGitHubRepositories.mockRejectedValue(
      new ApiError(404, "Not Found", {
        type: "https://cvoya.com/problems/unit-not-found",
        title: "Not Found",
        status: 404,
        detail: "UnitNotFound: unit was deleted.",
        code: "UnitNotFound",
        traceId: "00-github-wizard",
      }),
    );
    mocked.getGitHubInstallUrl.mockResolvedValue({
      url: "https://github.com/apps/spring-voyage/installations/new",
    });
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    await waitFor(() => {
      expect(
        screen.getByRole("link", { name: /install github app/i }),
      ).toBeInTheDocument();
    });
    expect(screen.getByText(/Unit not found\./)).toBeInTheDocument();
    expect(
      screen.getByText(/It may have been deleted\. Refresh the page/),
    ).toBeInTheDocument();
    expect(screen.queryByText(/API error 404/)).not.toBeInTheDocument();
    expect(screen.queryByText(/UnitNotFound:/)).not.toBeInTheDocument();
  });

  it("passes axe smoke with the install-app banner visible (#599)", async () => {
    mocked.listGitHubRepositories.mockResolvedValue([]);
    mocked.getGitHubInstallUrl.mockResolvedValue({
      url: "https://github.com/apps/spring-voyage/installations/new",
    });
    const onChange = vi.fn();

    let container!: HTMLElement;
    await act(async () => {
      const result = render(<GitHubConnectorWizardStep onChange={onChange} />);
      container = result.container;
    });

    await waitFor(() =>
      expect(
        screen.getByRole("link", { name: /install github app/i }),
      ).toBeInTheDocument(),
    );
    await expectNoAxeViolations(container);
  });

  it("renders the friendly disabled panel when the connector is not configured (#1186)", async () => {
    mocked.listGitHubRepositories.mockRejectedValue(
      new ApiError(404, "Not Found", {
        type: "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        title: "GitHub connector is not configured",
        status: 404,
        detail: "GitHub App not configured on this deployment.",
        disabled: true,
        reason: "GitHub App not configured on this deployment.",
      }),
    );
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    await waitFor(() =>
      expect(
        screen.getByText(
          /GitHub connector not configured on this deployment\./,
        ),
      ).toBeInTheDocument(),
    );
    expect(
      screen.getByRole("link", { name: /view deployment guide/i }),
    ).toBeInTheDocument();
    expect(screen.queryByText(/API error 404/)).not.toBeInTheDocument();
    expect(mocked.getGitHubInstallUrl).not.toHaveBeenCalled();
  });

  it("links the GitHub OAuth session automatically from the callback popup", async () => {
    mocked.listGitHubRepositories.mockRejectedValueOnce(
      new ApiError(401, "Unauthorized", {
        missingOAuth: true,
        reason: "No GitHub OAuth session was supplied.",
        authorizeUrl: "https://github.com/login/oauth/authorize?state=old",
        state: "old",
      }),
    );
    mocked.beginGitHubOAuthAuthorize.mockResolvedValue({
      authorizeUrl: "https://github.com/login/oauth/authorize?state=fresh",
      state: "fresh",
    });
    mocked.listGitHubRepositories.mockResolvedValue([repoFixture]);
    mocked.listGitHubCollaborators.mockResolvedValue([]);
    const focus = vi.fn();
    const close = vi.fn();
    const popup = {
      focus,
      close,
      closed: false,
      location: { href: "" },
    } as unknown as Window;
    const open = vi
      .spyOn(window, "open")
      .mockReturnValue(popup);
    const onChange = vi.fn();

    try {
      await act(async () => {
        render(<GitHubConnectorWizardStep onChange={onChange} />);
      });

      await screen.findByTestId("github-missing-oauth");

      await act(async () => {
        fireEvent.click(screen.getByTestId("github-link-account"));
      });

      expect(mocked.beginGitHubOAuthAuthorize).toHaveBeenCalledWith({
        clientState: expect.stringContaining("targetOrigin"),
      });
      expect(open).toHaveBeenCalledWith(
        "",
        "spring-voyage-github-oauth",
        "popup,width=720,height=760",
      );
      expect(focus).toHaveBeenCalled();
      expect(popup.location.href).toBe(
        "https://github.com/login/oauth/authorize?state=fresh",
      );

      await act(async () => {
        window.dispatchEvent(
          new MessageEvent("message", {
            origin: window.location.origin,
            data: {
              type: "spring-voyage:github-oauth-session",
              sessionId: "sess-auto",
              login: "octocat",
            },
          }),
        );
      });

      await waitFor(() =>
        expect(mocked.listGitHubRepositories).toHaveBeenLastCalledWith(
          "sess-auto",
        ),
      );
      expect(
        window.sessionStorage.getItem(
          "springvoyage:github-oauth-session-id",
        ),
      ).toBe("sess-auto");
      await waitFor(() =>
        expect(screen.queryByTestId("github-missing-oauth")).toBeNull(),
      );
      await waitFor(() => {
        const repoSelect = screen.getByLabelText(
          APP_REPO_DROPDOWN_LABEL,
        ) as HTMLSelectElement;
        expect(
          Array.from(repoSelect.options).some(
            (o) => o.value === "acme/platform",
          ),
        ).toBe(true);
      });
    } finally {
      open.mockRestore();
      window.sessionStorage.removeItem("springvoyage:github-oauth-session-id");
    }
  });

  // Safari-safe fallback: when the popup→opener postMessage / localStorage
  // handoff is blocked (Safari storage partitioning), the wizard polls the
  // server result store by the nonce it put in clientState and accepts the
  // session from the poll — no browser message is dispatched here.
  it("recovers the OAuth session via server-poll when the browser handoff is blocked", async () => {
    mocked.listGitHubRepositories.mockRejectedValueOnce(
      new ApiError(401, "Unauthorized", {
        missingOAuth: true,
        reason: "No GitHub OAuth session was supplied.",
        authorizeUrl: "https://github.com/login/oauth/authorize?state=old",
        state: "old",
      }),
    );
    mocked.beginGitHubOAuthAuthorize.mockResolvedValue({
      authorizeUrl: "https://github.com/login/oauth/authorize?state=fresh",
      state: "fresh",
    });
    mocked.listGitHubRepositories.mockResolvedValue([repoFixture]);
    mocked.listGitHubCollaborators.mockResolvedValue([]);
    mocked.getGitHubOAuthResult.mockResolvedValue({
      ready: true,
      sessionId: "sess-poll",
      login: "octocat",
      error: null,
      reason: null,
    });
    const popup = {
      focus: vi.fn(),
      close: vi.fn(),
      closed: false,
      location: { href: "" },
    } as unknown as Window;
    const open = vi.spyOn(window, "open").mockReturnValue(popup);
    const onChange = vi.fn();

    try {
      await act(async () => {
        render(<GitHubConnectorWizardStep onChange={onChange} />);
      });
      await screen.findByTestId("github-missing-oauth");

      vi.useFakeTimers();
      await act(async () => {
        fireEvent.click(screen.getByTestId("github-link-account"));
      });

      // The authorize call carries a poll nonce in clientState.
      const clientState = mocked.beginGitHubOAuthAuthorize.mock.calls.at(-1)![0]
        .clientState as string;
      expect(clientState).toMatch(/"nonce":"[0-9a-f]{32}"/);

      // No postMessage is dispatched (Safari). Advance past the first poll
      // tick (1.5s): the ready result lands and the session is accepted.
      await act(async () => {
        await vi.advanceTimersByTimeAsync(1600);
      });

      expect(mocked.getGitHubOAuthResult).toHaveBeenCalled();
      expect(mocked.listGitHubRepositories).toHaveBeenLastCalledWith(
        "sess-poll",
      );
      expect(
        window.sessionStorage.getItem("springvoyage:github-oauth-session-id"),
      ).toBe("sess-poll");
    } finally {
      vi.useRealTimers();
      open.mockRestore();
      window.sessionStorage.removeItem("springvoyage:github-oauth-session-id");
    }
  });

  it("re-fetches repositories when the Recheck button is clicked (#1132)", async () => {
    mocked.listGitHubRepositories.mockResolvedValueOnce([]);
    mocked.getGitHubInstallUrl.mockResolvedValue({
      url: "https://github.com/apps/spring-voyage/installations/new",
    });
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    const recheck = await screen.findByTestId(
      "github-recheck-installations",
    );
    expect(mocked.listGitHubRepositories).toHaveBeenCalledTimes(1);
    expect(
      screen.getByText(/No GitHub repositories visible\./),
    ).toBeInTheDocument();

    mocked.listGitHubRepositories.mockResolvedValueOnce([repoFixture]);
    mocked.listGitHubCollaborators.mockResolvedValue([]);

    await act(async () => {
      fireEvent.click(recheck);
    });

    await waitFor(() => {
      expect(mocked.listGitHubRepositories).toHaveBeenCalledTimes(2);
    });
    await waitFor(() => {
      expect(
        screen.queryByText(/No GitHub repositories visible\./),
      ).not.toBeInTheDocument();
    });
    const repoSelect = screen.getByLabelText(
      APP_REPO_DROPDOWN_LABEL,
    ) as HTMLSelectElement;
    expect(
      Array.from(repoSelect.options).some((o) => o.value === "acme/platform"),
    ).toBe(true);
  });

  it("does not render the Recheck button when the connector is disabled (#1132)", async () => {
    mocked.listGitHubRepositories.mockRejectedValue(
      new ApiError(404, "Not Found", {
        disabled: true,
        reason: "GitHub App not configured on this deployment.",
      }),
    );
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    await waitFor(() =>
      expect(
        screen.getByText(/GitHub connector not configured on this deployment\./),
      ).toBeInTheDocument(),
    );
    expect(
      screen.queryByTestId("github-recheck-installations"),
    ).not.toBeInTheDocument();
  });

  // #1127: by default the wizard checks the "Connector defaults" toggle
  // and the per-event row is informational — checks reflect the
  // server's DefaultEvents (issues, pull_request, issue_comment) and
  // every event input is disabled. The wire shape omits `events` so
  // the server resolves the set itself.
  it("starts with Connector defaults checked and event row disabled (#1127)", async () => {
    mocked.listGitHubRepositories.mockResolvedValue([repoFixture]);
    mocked.listGitHubCollaborators.mockResolvedValue([]);
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    await waitFor(() =>
      expect(mocked.listGitHubRepositories).toHaveBeenCalled(),
    );

    const toggle = screen.getByTestId(
      "github-events-use-defaults",
    ) as HTMLInputElement;
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

    await act(async () => {
      fireEvent.change(screen.getByLabelText(APP_REPO_DROPDOWN_LABEL), {
        target: { value: "acme/platform" },
      });
    });

    await waitFor(() => {
      const last = onChange.mock.calls.at(-1)?.[0];
      expect(last).toEqual(
        expect.objectContaining({
          repo: "acme/platform",
          appInstallationId: 7777,
        }),
      );
      expect(last?.events).toBeUndefined();
    });
  });

  // #1127: unchecking "Connector defaults" enables the per-event row and
  // pre-populates it with the same DefaultEvents the operator was
  // already living with — they don't restart from an empty form.
  it("enables and pre-populates the event row when Connector defaults is unchecked (#1127)", async () => {
    mocked.listGitHubRepositories.mockResolvedValue([repoFixture]);
    mocked.listGitHubCollaborators.mockResolvedValue([]);
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    await waitFor(() =>
      expect(mocked.listGitHubRepositories).toHaveBeenCalled(),
    );

    await act(async () => {
      fireEvent.change(screen.getByLabelText(APP_REPO_DROPDOWN_LABEL), {
        target: { value: "acme/platform" },
      });
    });

    const toggle = screen.getByTestId(
      "github-events-use-defaults",
    ) as HTMLInputElement;
    await act(async () => {
      fireEvent.click(toggle);
    });
    expect(toggle.checked).toBe(false);

    const issues = screen.getByLabelText("issues") as HTMLInputElement;
    const release = screen.getByLabelText("release") as HTMLInputElement;
    expect(issues).not.toBeDisabled();
    expect(release).not.toBeDisabled();
    expect(issues.checked).toBe(true);
    expect(release.checked).toBe(false);

    await act(async () => {
      fireEvent.click(release);
    });

    await waitFor(() => {
      const last = onChange.mock.calls.at(-1)?.[0];
      expect(last?.events).toEqual(
        expect.arrayContaining([
          "issues",
          "pull_request",
          "issue_comment",
          "release",
        ]),
      );
      expect(last?.events).not.toContain("push");
    });

    await act(async () => {
      fireEvent.click(toggle);
    });
    expect(toggle.checked).toBe(true);
    await waitFor(() => {
      const last = onChange.mock.calls.at(-1)?.[0];
      expect(last?.events).toBeUndefined();
    });
  });

  it("starts with Connector defaults UNchecked when initialValue carries an explicit events list (#1127)", async () => {
    mocked.listGitHubRepositories.mockResolvedValue([repoFixture]);
    mocked.listGitHubCollaborators.mockResolvedValue([]);
    const onChange = vi.fn();

    await act(async () => {
      render(
        <GitHubConnectorWizardStep
          onChange={onChange}
          initialValue={{
            repo: "acme/platform",
            appInstallationId: 7777,
            events: ["push", "release"],
            reviewer: undefined,
          }}
        />,
      );
    });

    await waitFor(() =>
      expect(mocked.listGitHubRepositories).toHaveBeenCalled(),
    );

    const toggle = screen.getByTestId(
      "github-events-use-defaults",
    ) as HTMLInputElement;
    expect(toggle.checked).toBe(false);
    const push = screen.getByLabelText("push") as HTMLInputElement;
    const release = screen.getByLabelText("release") as HTMLInputElement;
    const issues = screen.getByLabelText("issues") as HTMLInputElement;
    expect(push.checked).toBe(true);
    expect(release.checked).toBe(true);
    expect(issues.checked).toBe(false);
    expect(push).not.toBeDisabled();

    await waitFor(() => {
      const last = onChange.mock.calls.at(-1)?.[0];
      expect(last?.events).toEqual(["push", "release"]);
    });
  });

  it("hydrates from initialValue when provided", async () => {
    mocked.listGitHubRepositories.mockResolvedValue([repoFixture]);
    mocked.listGitHubCollaborators.mockResolvedValue([]);
    const onChange = vi.fn();

    await act(async () => {
      render(
        <GitHubConnectorWizardStep
          onChange={onChange}
          initialValue={{
            repo: "acme/platform",
            appInstallationId: 7777,
            events: undefined,
            reviewer: undefined,
          }}
        />,
      );
    });

    await waitFor(() =>
      expect(mocked.listGitHubRepositories).toHaveBeenCalled(),
    );

    const qualifiedInput = screen.getByLabelText(
      QUALIFIED_REPO_INPUT_LABEL,
    ) as HTMLInputElement;
    expect(qualifiedInput.value).toBe("acme/platform");
  });

  // ADR-0047 §11: initial value carrying `pat_secret_name` seeds the
  // PAT branch on mount; the wizard re-bubbles the same shape so the
  // wizard's persistence doesn't drop the secret name on a step
  // re-enter.
  it("hydrates the PAT auth-choice from initialValue.pat_secret_name (ADR-0047 §11)", async () => {
    mocked.listGitHubRepositories.mockResolvedValue([]);
    mocked.getGitHubInstallUrl.mockResolvedValue({ url: "" });
    const onChange = vi.fn();

    await act(async () => {
      render(
        <GitHubConnectorWizardStep
          onChange={onChange}
          initialValue={{
            repo: "octocat/Hello-World",
            pat_secret_name: "binding/abc/github/pat",
            events: undefined,
            reviewer: undefined,
          }}
        />,
      );
    });

    const patRadio = await screen.findByTestId(
      "github-auth-choice-pat",
    );
    expect((patRadio as HTMLInputElement).checked).toBe(true);

    // With a secret already wired, the PAT sub-step shows the saved
    // indicator naming the secret — not a raw-token input.
    const saved = screen.getByTestId("github-pat-saved");
    expect(saved).toHaveTextContent("binding/abc/github/pat");

    await waitFor(() => {
      const last = onChange.mock.calls.at(-1)?.[0];
      expect(last).toEqual(
        expect.objectContaining({
          repo: "octocat/Hello-World",
          pat_secret_name: "binding/abc/github/pat",
        }),
      );
    });
  });

  // #2956: the auth choice renders unconditionally, so an operator can
  // reach the PAT path even when the GitHub App is not configured
  // (disabledReason). The paste-PAT path needs neither a configured App
  // nor an OAuth session — the token is stored as a tenant secret and
  // the binding-create endpoint accepts a PAT-only config.
  it("reaches the PAT path and completes a binding when the App is not configured (#2956)", async () => {
    mocked.listGitHubRepositories.mockRejectedValue(
      new ApiError(404, "Not Found", {
        disabled: true,
        reason: "GitHub App not configured on this deployment.",
      }),
    );
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    // App mode (the default) surfaces the not-configured guidance, but
    // the auth choice is reachable while the repository control is not.
    await waitFor(() =>
      expect(
        screen.getByText(
          /GitHub connector not configured on this deployment\./,
        ),
      ).toBeInTheDocument(),
    );
    expect(screen.getByTestId("github-auth-choice")).toBeInTheDocument();
    expect(screen.queryByLabelText(QUALIFIED_REPO_INPUT_LABEL)).toBeNull();

    // Flip to PAT: the not-configured panel disappears and the
    // free-text repo + PAT secret controls render without an App.
    await act(async () => {
      fireEvent.click(screen.getByTestId("github-auth-choice-pat"));
    });

    expect(
      screen.queryByText(
        /GitHub connector not configured on this deployment\./,
      ),
    ).toBeNull();
    expect(
      screen.getByLabelText(QUALIFIED_REPO_INPUT_LABEL),
    ).toBeInTheDocument();

    mocked.createTenantSecret.mockResolvedValue({
      name: "ignored",
      scope: "Tenant",
      createdAt: "2026-01-01T00:00:00Z",
    });
    await act(async () => {
      fireEvent.change(screen.getByLabelText(QUALIFIED_REPO_INPUT_LABEL), {
        target: { value: "octocat/Hello-World" },
      });
    });
    await act(async () => {
      fireEvent.change(screen.getByTestId("github-pat-token"), {
        target: { value: "ghp_token" },
      });
    });
    await act(async () => {
      fireEvent.click(screen.getByTestId("github-pat-save"));
    });

    await waitFor(() =>
      expect(mocked.createTenantSecret).toHaveBeenCalled(),
    );
    const savedName = mocked.createTenantSecret.mock.calls.at(-1)![0].name;

    await waitFor(() => {
      const last = onChange.mock.calls.at(-1)?.[0];
      expect(last).toEqual(
        expect.objectContaining({
          repo: "octocat/Hello-World",
          pat_secret_name: savedName,
        }),
      );
      expect(last?.appInstallationId).toBeUndefined();
    });
  });

  // #2956: same reachability when the operator has not linked a GitHub
  // OAuth session (missingOAuth). The paste-PAT path needs no OAuth
  // session and no SV GitHub App.
  it("reaches the PAT path and completes a binding when no OAuth session is linked (#2956)", async () => {
    mocked.listGitHubRepositories.mockRejectedValue(
      new ApiError(401, "Unauthorized", {
        missingOAuth: true,
        reason: "No GitHub OAuth session was supplied.",
        authorizeUrl: "https://github.com/login/oauth/authorize?state=x",
        state: "x",
      }),
    );
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    // App mode surfaces the link-account guidance; PAT stays reachable.
    await screen.findByTestId("github-missing-oauth");
    expect(screen.getByTestId("github-auth-choice")).toBeInTheDocument();

    await act(async () => {
      fireEvent.click(screen.getByTestId("github-auth-choice-pat"));
    });

    // PAT mode hides the App-only link-account panel and renders the
    // manual path.
    expect(screen.queryByTestId("github-missing-oauth")).toBeNull();

    mocked.createTenantSecret.mockResolvedValue({
      name: "ignored",
      scope: "Tenant",
      createdAt: "2026-01-01T00:00:00Z",
    });
    await act(async () => {
      fireEvent.change(screen.getByLabelText(QUALIFIED_REPO_INPUT_LABEL), {
        target: { value: "octocat/Hello-World" },
      });
    });
    await act(async () => {
      fireEvent.change(screen.getByTestId("github-pat-token"), {
        target: { value: "ghp_token" },
      });
    });
    await act(async () => {
      fireEvent.click(screen.getByTestId("github-pat-save"));
    });

    await waitFor(() =>
      expect(mocked.createTenantSecret).toHaveBeenCalled(),
    );
    const savedName = mocked.createTenantSecret.mock.calls.at(-1)![0].name;

    await waitFor(() => {
      const last = onChange.mock.calls.at(-1)?.[0];
      expect(last).toEqual(
        expect.objectContaining({
          repo: "octocat/Hello-World",
          pat_secret_name: savedName,
        }),
      );
      expect(last?.appInstallationId).toBeUndefined();
    });
  });
});
