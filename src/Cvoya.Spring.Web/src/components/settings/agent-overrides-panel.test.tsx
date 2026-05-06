import {
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import type { AgentResponse, SecretMetadata } from "@/lib/api/types";

// Mock the API client. Only the methods the panel actually invokes need
// to be defined — anything else left undefined would throw if called.
const listAgents = vi.fn<() => Promise<AgentResponse[]>>();
const listAgentSecrets =
  vi.fn<(agentId: string) => Promise<{ secrets: SecretMetadata[] }>>();
const createAgentSecret = vi.fn();
const deleteAgentSecret = vi.fn();

vi.mock("@/lib/api/client", () => ({
  api: {
    listAgents: () => listAgents(),
    listAgentSecrets: (id: string) => listAgentSecrets(id),
    createAgentSecret: (...args: unknown[]) => createAgentSecret(...args),
    deleteAgentSecret: (...args: unknown[]) => deleteAgentSecret(...args),
  },
}));

const toastMock = vi.fn();
vi.mock("@/components/ui/toast", () => ({
  useToast: () => ({ toast: toastMock }),
}));

import { AgentOverridesPanel } from "./agent-overrides-panel";

function makeAgent(overrides: Partial<AgentResponse> = {}): AgentResponse {
  return {
    id: "actor-id",
    name: "ada",
    displayName: "Ada",
    description: "",
    role: null,
    registeredAt: new Date().toISOString(),
    model: null,
    specialty: null,
    enabled: true,
    executionMode: "Auto",
    parentUnit: null,
    ...overrides,
  } as AgentResponse;
}

describe("AgentOverridesPanel (#1744)", () => {
  beforeEach(() => {
    listAgents.mockReset();
    listAgentSecrets.mockReset();
    createAgentSecret.mockReset();
    deleteAgentSecret.mockReset();
    toastMock.mockReset();
  });

  afterEach(() => {
    vi.clearAllMocks();
  });

  it("renders the empty state until the operator picks an agent and never lists secrets up-front", async () => {
    listAgents.mockResolvedValue([
      makeAgent({ name: "ada", displayName: "Ada" }),
      makeAgent({ name: "babbage", displayName: "Babbage" }),
    ]);

    render(<AgentOverridesPanel />);

    expect(
      await screen.findByTestId("agent-overrides-empty-state"),
    ).toBeInTheDocument();
    // listAgentSecrets must NOT be called before an agent is picked —
    // the empty state is a hint, not a fetch.
    expect(listAgentSecrets).not.toHaveBeenCalled();
  });

  it("loads the secret list when an agent is selected", async () => {
    listAgents.mockResolvedValue([
      makeAgent({ name: "ada", displayName: "Ada" }),
    ]);
    listAgentSecrets.mockResolvedValue({
      secrets: [
        {
          name: "anthropic-api-key",
          scope: "Agent",
          createdAt: new Date().toISOString(),
        },
      ] as SecretMetadata[],
    });

    render(<AgentOverridesPanel />);

    const select = await screen.findByTestId(
      "agent-overrides-agent-select",
    );
    fireEvent.change(select, { target: { value: "ada" } });

    await waitFor(() => {
      expect(listAgentSecrets).toHaveBeenCalledWith("ada");
    });
    await waitFor(() => {
      expect(
        screen.getByTestId("agent-override-row-anthropic-api-key"),
      ).toBeInTheDocument();
    });
  });

  it("posts to createAgentSecret without a propagate field when the operator submits the form", async () => {
    listAgents.mockResolvedValue([
      makeAgent({ name: "ada", displayName: "Ada" }),
    ]);
    listAgentSecrets.mockResolvedValue({ secrets: [] });
    createAgentSecret.mockResolvedValue({});

    render(<AgentOverridesPanel />);

    const select = await screen.findByTestId(
      "agent-overrides-agent-select",
    );
    fireEvent.change(select, { target: { value: "ada" } });

    const nameInput = await screen.findByTestId("agent-override-name");
    fireEvent.change(nameInput, { target: { value: "openai-api-key" } });

    const valueInput = screen.getByTestId("agent-override-value");
    fireEvent.change(valueInput, { target: { value: "sk-secret" } });

    fireEvent.click(screen.getByTestId("agent-override-submit"));

    await waitFor(() => {
      expect(createAgentSecret).toHaveBeenCalledTimes(1);
    });

    const [agentId, body] = createAgentSecret.mock.calls[0] as [
      string,
      Record<string, unknown>,
    ];
    expect(agentId).toBe("ada");
    expect(body).toEqual({
      name: "openai-api-key",
      value: "sk-secret",
      externalStoreKey: undefined,
    });
    // Crucial #1744 contract: agent scope has no descendants and the
    // panel must not surface a propagate toggle. The body sent on the
    // wire must therefore not carry that field at all.
    expect(body).not.toHaveProperty("propagate");
  });

  it("does not render a propagate toggle on the agent-scope add form", async () => {
    listAgents.mockResolvedValue([
      makeAgent({ name: "ada", displayName: "Ada" }),
    ]);
    listAgentSecrets.mockResolvedValue({ secrets: [] });

    render(<AgentOverridesPanel />);

    const select = await screen.findByTestId(
      "agent-overrides-agent-select",
    );
    fireEvent.change(select, { target: { value: "ada" } });

    // Wait for the form to render after the agent is selected.
    await screen.findByTestId("agent-override-name");

    // The unit Secrets tab uses this exact testid for its propagate
    // checkbox (#1741); the agent-scope panel must not.
    expect(
      screen.queryByTestId("unit-secret-propagate-toggle"),
    ).toBeNull();
    expect(
      screen.queryByText(/Propagate to descendants/i),
    ).toBeNull();
  });

  it("filters the agent list by the typeahead query", async () => {
    listAgents.mockResolvedValue([
      makeAgent({ name: "ada", displayName: "Ada" }),
      makeAgent({ name: "babbage", displayName: "Babbage" }),
    ]);

    render(<AgentOverridesPanel />);

    const filter = await screen.findByTestId("agent-overrides-filter");
    fireEvent.change(filter, { target: { value: "babb" } });

    const select = screen.getByTestId(
      "agent-overrides-agent-select",
    ) as HTMLSelectElement;
    const optionValues = Array.from(select.options).map((o) => o.value);
    expect(optionValues).toContain("babbage");
    expect(optionValues).not.toContain("ada");
  });
});
