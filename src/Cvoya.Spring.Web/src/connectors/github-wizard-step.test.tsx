import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

// The wizard-step component calls the shared `api` client for
// installations / install-url. Mock the module before importing the
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
      listGitHubInstallations: vi.fn(),
      getGitHubInstallUrl: vi.fn(),
    },
  };
});

import { ApiError, api } from "@/lib/api/client";
import { expectNoAxeViolations } from "@/test/a11y";
import { GitHubConnectorWizardStep } from "@connector-github/connector-wizard-step";

const mocked = vi.mocked(api);

describe("GitHubConnectorWizardStep", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("bubbles null until both owner and repo are filled", async () => {
    mocked.listGitHubInstallations.mockResolvedValue([]);
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    // Initial render produces null (required fields empty).
    await waitFor(() => expect(onChange).toHaveBeenCalledWith(null));

    const owner = screen.getByPlaceholderText("acme");
    await act(async () => {
      fireEvent.change(owner, { target: { value: "acme" } });
    });

    // Owner alone still isn't enough — repo is also required.
    await waitFor(() => {
      const lastCall = onChange.mock.calls.at(-1);
      expect(lastCall?.[0]).toBeNull();
    });
  });

  it("emits a typed config payload once owner + repo are filled", async () => {
    mocked.listGitHubInstallations.mockResolvedValue([]);
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    await act(async () => {
      fireEvent.change(screen.getByPlaceholderText("acme"), {
        target: { value: "acme" },
      });
      fireEvent.change(screen.getByPlaceholderText("platform"), {
        target: { value: "platform" },
      });
    });

    await waitFor(() => {
      const last = onChange.mock.calls.at(-1)?.[0];
      expect(last).not.toBeNull();
      expect(last).toEqual(
        expect.objectContaining({
          owner: "acme",
          repo: "platform",
        }),
      );
    });
  });

  it("shows the install-app banner when no installations are visible", async () => {
    mocked.listGitHubInstallations.mockResolvedValue([]);
    mocked.getGitHubInstallUrl.mockResolvedValue({ url: "" });
    const onChange = vi.fn();

    await act(async () => {
      render(<GitHubConnectorWizardStep onChange={onChange} />);
    });

    await waitFor(() =>
      expect(
        screen.getByText(/No GitHub App installations found\./),
      ).toBeInTheDocument(),
    );
  });

  it("renders the install-app link when the list comes back empty (#599)", async () => {
    // The previous implementation only fetched the install URL on the
    // catch branch, so a platform with the App configured but no
    // installations surfaced a banner with no call-to-action link.
    mocked.listGitHubInstallations.mockResolvedValue([]);
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

  it("renders the install-app link when listing installations throws", async () => {
    mocked.listGitHubInstallations.mockRejectedValue(
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
    mocked.listGitHubInstallations.mockResolvedValue([]);
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
    mocked.listGitHubInstallations.mockRejectedValue(
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
        screen.getByText(/GitHub connector not configured on this deployment\./),
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
    mocked.listGitHubInstallations.mockResolvedValue([]);
    const onChange = vi.fn();

    await act(async () => {
      render(
        <GitHubConnectorWizardStep
          onChange={onChange}
          initialValue={{
            owner: "prefilled-owner",
            repo: "prefilled-repo",
            appInstallationId: undefined,
            events: undefined,
          }}
        />,
      );
    });

    expect(
      screen.getByDisplayValue("prefilled-owner"),
    ).toBeInTheDocument();
    expect(screen.getByDisplayValue("prefilled-repo")).toBeInTheDocument();
  });
});
