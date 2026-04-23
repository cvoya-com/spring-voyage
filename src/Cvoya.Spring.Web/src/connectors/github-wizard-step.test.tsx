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

describe("GitHubConnectorWizardStep", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("bubbles null until a repository is selected (#1133)", async () => {
    // Empty repo list → the dropdown stays empty and onChange must remain
    // null (the wizard refuses to bundle a half-filled config).
    mocked.listGitHubRepositories.mockResolvedValue([]);
    mocked.getGitHubInstallUrl.mockResolvedValue({ url: "" });
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    await waitFor(() => expect(onChange).toHaveBeenCalledWith(null));
  });

  it("emits a typed config payload once a repository is selected (#1133)", async () => {
    // Selecting a repo from the live list must derive owner / repo /
    // installationId from the row and bubble them up — the wizard never
    // re-asks the user to type those fields anymore.
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
      fireEvent.change(screen.getByLabelText("Repository"), {
        target: { value: "acme/platform" },
      });
    });

    await waitFor(() => {
      const last = onChange.mock.calls.at(-1)?.[0];
      expect(last).toEqual(
        expect.objectContaining({
          owner: "acme",
          repo: "platform",
          appInstallationId: 7777,
        }),
      );
    });
  });

  it("re-fetches collaborators when the repo changes and includes the picked reviewer (#1133)", async () => {
    // The Reviewer dropdown is populated by /list-collaborators for the
    // currently-selected repo and clears whenever the repo changes.
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
      fireEvent.change(screen.getByLabelText("Repository"), {
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
          owner: "acme",
          repo: "platform",
          reviewer: "alice",
        }),
      );
    });

    // Switching the repository must clear the previously-chosen reviewer
    // (collaborators are repo-scoped) and re-fetch.
    await act(async () => {
      fireEvent.change(screen.getByLabelText("Repository"), {
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

  it("renders the install-app link when the list comes back empty (#599)", async () => {
    // Carry-over from #599: a platform with the App configured but no
    // installations must still surface a call-to-action link, not just a
    // dead-end banner.
    mocked.listGitHubRepositories.mockResolvedValue([]);
    mocked.getGitHubInstallUrl.mockResolvedValue({
      url: "https://github.com/apps/spring-voyage/installations/new",
    });
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    await waitFor(() => {
      const link = screen.getByRole("link", { name: /install github app/i });
      expect(link).toHaveAttribute(
        "href",
        "https://github.com/apps/spring-voyage/installations/new",
      );
      expect(link).toHaveAttribute("target", "_blank");
      expect(link).toHaveAttribute("rel", "noopener noreferrer");
    });
    expect(mocked.getGitHubInstallUrl).toHaveBeenCalledTimes(1);
  });

  it("renders the install-app link when listing repositories throws", async () => {
    mocked.listGitHubRepositories.mockRejectedValue(
      new Error("502 Bad Gateway"),
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
    expect(
      screen.getByText(/502 Bad Gateway/, { exact: false }),
    ).toBeInTheDocument();
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
    // Server returns the structured Problem+JSON `{ disabled: true,
    // reason: "GitHub App not configured on this deployment." }`. The
    // wizard must NOT leak the raw RFC 9110 envelope into the UI; it
    // should render the deployment-guide panel and skip the
    // install-url fetch (which would 404 with the same payload).
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
    // The raw API error envelope must not appear anywhere in the UI.
    expect(screen.queryByText(/API error 404/)).not.toBeInTheDocument();
    // No install-url fetch attempted — the endpoint would 404 with the
    // same disabled payload and there is nothing to render.
    expect(mocked.getGitHubInstallUrl).not.toHaveBeenCalled();
  });

  it("hydrates from initialValue when provided", async () => {
    // When the user navigates back to the wizard step, the previously
    // selected repository must already be the chosen value in the
    // dropdown. We synthesise a row in the live list that matches the
    // initialValue so the option exists for selection.
    mocked.listGitHubRepositories.mockResolvedValue([repoFixture]);
    mocked.listGitHubCollaborators.mockResolvedValue([]);
    const onChange = vi.fn();

    await act(async () => {
      render(
        <GitHubConnectorWizardStep
          onChange={onChange}
          initialValue={{
            owner: "acme",
            repo: "platform",
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

    const repoSelect = screen.getByLabelText("Repository") as HTMLSelectElement;
    expect(repoSelect.value).toBe("acme/platform");
  });
});
