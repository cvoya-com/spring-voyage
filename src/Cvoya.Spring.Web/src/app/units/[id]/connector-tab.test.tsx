import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

// Mock `@/lib/api/client` BEFORE importing the component so the component's
// import binds to the mocked surface. Each test then configures the relevant
// handlers. The variable name must start with `mock` for Vitest hoisting.
const mockApi = vi.hoisted(() => ({
  getUnitConnector: vi.fn(),
  setUnitConnector: vi.fn(),
  listGitHubInstallations: vi.fn(),
  getGitHubInstallUrl: vi.fn(),
}));

vi.mock("@/lib/api/client", () => ({
  api: mockApi,
}));

vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: vi.fn() }),
}));

import { ConnectorTab } from "./connector-tab";

describe("ConnectorTab", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("renders the persisted config when the unit is already bound", async () => {
    mockApi.getUnitConnector.mockResolvedValue({
      type: "github",
      repo: { owner: "acme", name: "platform" },
      events: ["issues", "pull_request"],
      appInstallationId: 42,
      webhookId: 9999,
    });
    mockApi.listGitHubInstallations.mockResolvedValue([
      {
        installationId: 42,
        account: "acme",
        accountType: "Organization",
        repoSelection: "selected",
      },
    ]);

    render(<ConnectorTab unitId="engineering" />);

    // Owner + name fields are populated from the server response.
    const owner = (await screen.findByPlaceholderText(
      "acme",
    )) as HTMLInputElement;
    const name = (await screen.findByPlaceholderText(
      "platform",
    )) as HTMLInputElement;
    expect(owner.value).toBe("acme");
    expect(name.value).toBe("platform");

    // Webhook badge tells the user the /start handler has registered.
    expect(screen.getByText("Webhook registered")).toBeInTheDocument();
  });

  it("shows the install-app CTA when no installations exist", async () => {
    mockApi.getUnitConnector.mockResolvedValue({
      type: "github",
      repo: null,
      events: ["issues"],
      appInstallationId: null,
      webhookId: null,
    });
    mockApi.listGitHubInstallations.mockRejectedValue(
      new Error("App not configured"),
    );
    mockApi.getGitHubInstallUrl.mockResolvedValue({
      url: "https://github.com/apps/spring-voyage/installations/new",
    });

    render(<ConnectorTab unitId="engineering" />);

    const link = (await screen.findByText("Install App")) as HTMLAnchorElement;
    expect(link.getAttribute("href")).toBe(
      "https://github.com/apps/spring-voyage/installations/new",
    );
  });

  it("PUTs the connector config when Save is clicked", async () => {
    mockApi.getUnitConnector.mockResolvedValue({
      type: "github",
      repo: null,
      events: ["issues"],
      appInstallationId: null,
      webhookId: null,
    });
    mockApi.listGitHubInstallations.mockResolvedValue([]);
    mockApi.getGitHubInstallUrl.mockResolvedValue({ url: "https://x" });
    mockApi.setUnitConnector.mockResolvedValue({
      type: "github",
      repo: { owner: "acme", name: "platform" },
      events: ["issues"],
      appInstallationId: null,
      webhookId: null,
    });

    render(<ConnectorTab unitId="engineering" />);

    const owner = (await screen.findByPlaceholderText(
      "acme",
    )) as HTMLInputElement;
    const name = (await screen.findByPlaceholderText(
      "platform",
    )) as HTMLInputElement;
    fireEvent.change(owner, { target: { value: "acme" } });
    fireEvent.change(name, { target: { value: "platform" } });

    const save = screen.getByRole("button", { name: /save/i });
    fireEvent.click(save);

    await waitFor(() => {
      expect(mockApi.setUnitConnector).toHaveBeenCalledWith(
        "engineering",
        expect.objectContaining({
          type: "github",
          repo: { owner: "acme", name: "platform" },
        }),
      );
    });
  });
});
