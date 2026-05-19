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

    // While the PAT secret name is empty the wizard must NOT bubble a
    // payload — the PAT branch is incomplete.
    await waitFor(() => {
      const last = onChange.mock.calls.at(-1)?.[0];
      expect(last).toBeNull();
    });

    await act(async () => {
      fireEvent.change(screen.getByTestId("github-pat-secret-name"), {
        target: { value: "ops/github/pat" },
      });
    });

    await waitFor(() => {
      const last = onChange.mock.calls.at(-1)?.[0];
      expect(last).toEqual(
        expect.objectContaining({
          repo: "acme/platform",
          pat_secret_name: "ops/github/pat",
        }),
      );
      // App branch fields must not leak across the auth-choice
      // boundary.
      expect(last?.appInstallationId).toBeUndefined();
    });
  });

  // ADR-0047 §13: the auth-choice PAT path's "Authorize with GitHub"
  // button pre-mints a binding UUID, opens the popup, and listens for
  // the callback handoff. The handoff carries `patSecretName` +
  // `bindingId`; the wizard auto-fills the secret-name field.
  it("pre-mints a bindingId and accepts the OAuth-callback patSecretName (ADR-0047 §13)", async () => {
    mocked.listGitHubRepositories.mockResolvedValue([repoFixture]);
    mocked.listGitHubCollaborators.mockResolvedValue([]);
    mocked.beginGitHubOAuthAuthorize.mockResolvedValue({
      authorizeUrl: "https://github.com/login/oauth/authorize?state=fresh",
      state: "fresh",
    });
    const popup = {
      focus: vi.fn(),
      close: vi.fn(),
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

      await waitFor(() =>
        expect(mocked.listGitHubRepositories).toHaveBeenCalled(),
      );

      await act(async () => {
        fireEvent.change(screen.getByLabelText(APP_REPO_DROPDOWN_LABEL), {
          target: { value: "acme/platform" },
        });
      });

      await act(async () => {
        fireEvent.click(screen.getByTestId("github-auth-choice-pat"));
      });

      await act(async () => {
        fireEvent.click(screen.getByTestId("github-pat-authorize"));
      });

      await waitFor(() =>
        expect(mocked.beginGitHubOAuthAuthorize).toHaveBeenCalled(),
      );
      const lastCall = mocked.beginGitHubOAuthAuthorize.mock.calls.at(-1)![0];
      expect(lastCall).toEqual(
        expect.objectContaining({
          intent: "binding-wizard",
          bindingId: expect.stringMatching(/^[0-9a-f]{32}$/),
        }),
      );

      // Simulate the OAuth callback page's postMessage handoff.
      const bindingId = lastCall!.bindingId as string;
      await act(async () => {
        window.dispatchEvent(
          new MessageEvent("message", {
            origin: window.location.origin,
            data: {
              type: "spring-voyage:github-oauth-session",
              sessionId: "sess-bind",
              login: "octocat",
              patSecretName: `binding/${bindingId}/github/pat`,
              bindingId,
            },
          }),
        );
      });

      await waitFor(() => {
        const last = onChange.mock.calls.at(-1)?.[0];
        expect(last?.pat_secret_name).toBe(
          `binding/${bindingId}/github/pat`,
        );
        expect(last?.appInstallationId).toBeUndefined();
      });
    } finally {
      open.mockRestore();
    }
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

    const secret = screen.getByTestId(
      "github-pat-secret-name",
    ) as HTMLInputElement;
    expect(secret.value).toBe("binding/abc/github/pat");

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
});
