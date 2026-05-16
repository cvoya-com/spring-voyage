import type { ReactNode } from "react";

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { render, screen, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type {
  AgentDetailResponse,
  EffectiveToolResponse,
  UnitResponse,
} from "@/lib/api/types";

const getAgent = vi.fn<(id: string) => Promise<AgentDetailResponse>>();
const getUnit = vi.fn<(id: string) => Promise<UnitResponse>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    getAgent: (id: string) => getAgent(id),
    getUnit: (id: string) => getUnit(id),
  },
}));

import { ToolsPanel } from "./tools-panel";

function Wrapper({ children }: { children: ReactNode }) {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0, staleTime: 0 } },
  });
  return <QueryClientProvider client={client}>{children}</QueryClientProvider>;
}

const PLATFORM_TOOL: EffectiveToolResponse = {
  name: "sv.expertise.lookup",
  namespace: "sv",
  description: "Look up a unit's expertise.",
  provenance: "platform",
  inheritedFromUnitName: null,
};

const CONNECTOR_TOOL_DIRECT: EffectiveToolResponse = {
  name: "github.create_issue",
  namespace: "github",
  description: "Open a new GitHub issue.",
  provenance: "connector:github",
  inheritedFromUnitName: null,
};

const CONNECTOR_TOOL_INHERITED: EffectiveToolResponse = {
  name: "github.create_pull_request",
  namespace: "github",
  description: "Open a new GitHub pull request.",
  provenance: "connector:github",
  inheritedFromUnitName: "engineering",
};

const ARXIV_INHERITED: EffectiveToolResponse = {
  name: "arxiv.search",
  namespace: "arxiv",
  description: "Search arXiv papers.",
  provenance: "connector:arxiv",
  inheritedFromUnitName: "research",
};

const IMAGE_TOOL: EffectiveToolResponse = {
  name: "acme.transcode_audio",
  namespace: "acme",
  description: "Transcode audio with ffmpeg.",
  provenance: "image:sha256:abc123",
  inheritedFromUnitName: null,
};

function buildAgentDetail(
  effectiveTools: EffectiveToolResponse[],
  executionImage: string | null = null,
): AgentDetailResponse {
  return {
    agent: {
      id: "00000000000000000000000000000001",
      name: "00000000000000000000000000000001",
      displayName: "Ada",
      description: "Test agent",
      role: "reviewer",
      registeredAt: "2026-01-01T00:00:00Z",
      enabled: true,
      executionMode: "auto",
      parentUnit: "Engineering",
      effectiveTools,
      executionImage,
    },
    status: null,
  } as unknown as AgentDetailResponse;
}

function buildUnit(
  effectiveTools: EffectiveToolResponse[],
  executionImage: string | null = null,
): UnitResponse {
  return {
    id: "00000000000000000000000000000010",
    name: "00000000000000000000000000000010",
    displayName: "Engineering",
    description: "Builds stuff",
    registeredAt: "2026-01-01T00:00:00Z",
    status: "draft",
    model: null,
    color: null,
    enabled: true,
    effectiveTools,
    executionImage,
  } as unknown as UnitResponse;
}

describe("ToolsPanel (#2337 Sub D)", () => {
  beforeEach(() => {
    getAgent.mockReset();
    getUnit.mockReset();
  });

  it("renders the three sections — Platform, Connectors, Image", async () => {
    getUnit.mockResolvedValue(
      buildUnit([PLATFORM_TOOL, CONNECTOR_TOOL_DIRECT, IMAGE_TOOL]),
    );

    render(<ToolsPanel kind="Unit" id="engineering" />, { wrapper: Wrapper });

    const panel = await screen.findByTestId("tab-unit-tools");
    expect(panel).toBeInTheDocument();
    expect(
      panel.querySelector('[data-testid="tab-unit-tools-platform"]'),
    ).toBeInTheDocument();
    expect(
      panel.querySelector('[data-testid="tab-unit-tools-connectors"]'),
    ).toBeInTheDocument();
    expect(
      panel.querySelector('[data-testid="tab-unit-tools-image"]'),
    ).toBeInTheDocument();
  });

  it("collapses the Platform section into a <details> with the sv.* listing", async () => {
    getUnit.mockResolvedValue(buildUnit([PLATFORM_TOOL]));

    render(<ToolsPanel kind="Unit" id="engineering" />, { wrapper: Wrapper });

    await screen.findByTestId("tab-unit-tools");

    const platformSection = screen.getByTestId("tab-unit-tools-platform");
    const details = platformSection.querySelector("details");
    expect(details).not.toBeNull();
    expect(details?.hasAttribute("open")).toBe(false);

    const summary = details!.querySelector("summary");
    expect(summary?.textContent).toContain("Platform tools");
    expect(summary?.textContent).toContain("sv.*");

    const list = screen.getByTestId("tab-unit-tools-platform-list");
    expect(list).toBeInTheDocument();
    expect(
      screen.getByTestId("tab-unit-tools-platform-tool-sv.expertise.lookup"),
    ).toBeInTheDocument();
  });

  it("groups connector tools per binding and shows Enabled when direct", async () => {
    getUnit.mockResolvedValue(buildUnit([CONNECTOR_TOOL_DIRECT]));

    render(<ToolsPanel kind="Unit" id="engineering" />, { wrapper: Wrapper });

    await screen.findByTestId("tab-unit-tools");

    expect(
      screen.getByTestId("tab-unit-tools-connector-github"),
    ).toBeInTheDocument();
    expect(
      screen.getByTestId("tab-unit-tools-connector-github-enabled"),
    ).toBeInTheDocument();
    expect(
      screen.queryByTestId("tab-unit-tools-connector-github-inherited"),
    ).toBeNull();
  });

  it("shows the Inherited overlay (opacity + link) when every tool in a group is inherited", async () => {
    getAgent.mockResolvedValue(
      buildAgentDetail([CONNECTOR_TOOL_INHERITED, ARXIV_INHERITED]),
    );

    render(
      <ToolsPanel kind="Agent" id="ada" parentUnitId="engineering" />,
      { wrapper: Wrapper },
    );

    await screen.findByTestId("tab-agent-tools");

    const githubCard = screen.getByTestId("tab-agent-tools-connector-github");
    expect(githubCard.className).toMatch(/opacity-60/);
    expect(githubCard.dataset.inherited).toBe("true");

    const inheritedBadge = screen.getByTestId(
      "tab-agent-tools-connector-github-inherited",
    );
    expect(inheritedBadge.textContent).toContain("Inherited from engineering");

    const link = inheritedBadge.querySelector("a");
    expect(link?.getAttribute("href")).toBe(
      "?node=engineering&tab=Config&subtab=Tools",
    );

    // arxiv group is also inherited (different unit name).
    const arxivCard = screen.getByTestId("tab-agent-tools-connector-arxiv");
    expect(arxivCard.dataset.inherited).toBe("true");
  });

  it("renders Enabled (not Inherited) when a connector group mixes inherited + direct entries", async () => {
    getAgent.mockResolvedValue(
      buildAgentDetail([CONNECTOR_TOOL_DIRECT, CONNECTOR_TOOL_INHERITED]),
    );

    render(
      <ToolsPanel kind="Agent" id="ada" parentUnitId="engineering" />,
      { wrapper: Wrapper },
    );

    await screen.findByTestId("tab-agent-tools");

    const githubCard = screen.getByTestId("tab-agent-tools-connector-github");
    expect(githubCard.dataset.inherited).toBe("false");
    expect(
      screen.getByTestId("tab-agent-tools-connector-github-enabled"),
    ).toBeInTheDocument();
    expect(
      screen.queryByTestId("tab-agent-tools-connector-github-inherited"),
    ).toBeNull();
  });

  it("renders the Image section with the tool count and tool entries", async () => {
    getUnit.mockResolvedValue(buildUnit([IMAGE_TOOL]));

    render(<ToolsPanel kind="Unit" id="engineering" />, { wrapper: Wrapper });

    await screen.findByTestId("tab-unit-tools");

    const imageSection = screen.getByTestId("tab-unit-tools-image");
    // #2348: when the unit's executionImage is null the header falls
    // back to the literal "Image" string — the legacy digest-suffix
    // derivation from the image:<digest> provenance has been removed.
    expect(imageSection.textContent).toContain("Image");
    expect(imageSection.textContent).not.toContain("sha256:abc123");
    // Tool count chip.
    expect(imageSection.textContent).toContain("1 tool");
    expect(
      screen.getByTestId("tab-unit-tools-image-tool-acme.transcode_audio"),
    ).toBeInTheDocument();
  });

  it("renders the executionImage tag in the Image header when the server populates it (#2348, Unit)", async () => {
    getUnit.mockResolvedValue(
      buildUnit([IMAGE_TOOL], "acme/agent:v1.2"),
    );

    render(<ToolsPanel kind="Unit" id="engineering" />, { wrapper: Wrapper });

    await screen.findByTestId("tab-unit-tools");

    const imageSection = screen.getByTestId("tab-unit-tools-image");
    expect(imageSection.textContent).toContain("acme/agent:v1.2");
    // The digest-suffix derivation is gone — the header must not surface
    // the per-tool provenance digest any more.
    expect(imageSection.textContent).not.toContain("sha256:abc123");
  });

  it("renders the executionImage tag in the Image header when the server populates it (#2348, Agent)", async () => {
    getAgent.mockResolvedValue(
      buildAgentDetail([IMAGE_TOOL], "acme/agent:v1.2"),
    );

    render(
      <ToolsPanel kind="Agent" id="ada" parentUnitId="engineering" />,
      { wrapper: Wrapper },
    );

    await screen.findByTestId("tab-agent-tools");

    const imageSection = screen.getByTestId("tab-agent-tools-image");
    expect(imageSection.textContent).toContain("acme/agent:v1.2");
    expect(imageSection.textContent).not.toContain("sha256:abc123");
  });

  it("falls back to the literal 'Image' string when executionImage is null (#2348)", async () => {
    getUnit.mockResolvedValue(buildUnit([IMAGE_TOOL], null));

    render(<ToolsPanel kind="Unit" id="engineering" />, { wrapper: Wrapper });

    await screen.findByTestId("tab-unit-tools");

    const imageSection = screen.getByTestId("tab-unit-tools-image");
    expect(imageSection.textContent).toContain("Image");
    expect(imageSection.textContent).not.toContain("sha256:abc123");
  });

  it("renders the Image empty state when no image tools are declared", async () => {
    getUnit.mockResolvedValue(buildUnit([PLATFORM_TOOL]));

    render(<ToolsPanel kind="Unit" id="engineering" />, { wrapper: Wrapper });

    await screen.findByTestId("tab-unit-tools");

    expect(
      screen.getByTestId("tab-unit-tools-image-empty").textContent,
    ).toContain("This image declares no custom tools.");
    // The label falls back to "Image" when no image-tier entries exist.
    expect(screen.getByTestId("tab-unit-tools-image").textContent).toContain(
      "Image",
    );
  });

  it("renders all three sections gracefully when every tier is empty", async () => {
    getUnit.mockResolvedValue(buildUnit([]));

    render(<ToolsPanel kind="Unit" id="engineering" />, { wrapper: Wrapper });

    await screen.findByTestId("tab-unit-tools");

    // Platform tier still renders (even when empty) so the surface
    // stays predictable.
    expect(screen.getByTestId("tab-unit-tools-platform")).toBeInTheDocument();
    expect(
      screen.getByTestId("tab-unit-tools-platform-empty"),
    ).toBeInTheDocument();

    // Connector tier collapses to an explicit empty-state card with
    // CLI guidance.
    expect(
      screen.getByTestId("tab-unit-tools-connectors-empty").textContent,
    ).toContain("No connector tools granted");

    // Image tier surfaces the "no custom tools" copy.
    expect(
      screen.getByTestId("tab-unit-tools-image-empty"),
    ).toBeInTheDocument();
  });

  it("renders the loading status while the data hook is pending", () => {
    // Never-resolving promise → query stays in loading.
    getUnit.mockReturnValue(new Promise<UnitResponse>(() => {}));

    render(<ToolsPanel kind="Unit" id="engineering" />, { wrapper: Wrapper });

    expect(screen.getByTestId("tab-unit-tools-loading")).toBeInTheDocument();
  });

  it("renders the error surface when the data hook rejects", async () => {
    getUnit.mockRejectedValue(new Error("boom"));

    render(<ToolsPanel kind="Unit" id="engineering" />, { wrapper: Wrapper });

    await waitFor(() => {
      expect(screen.getByTestId("tab-unit-tools-error")).toBeInTheDocument();
    });
  });
});
