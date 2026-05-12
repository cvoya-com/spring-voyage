import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { type ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const getPackageRequiredCredentialsMock = vi.fn();
const getProviderCredentialStatusMock = vi.fn();
vi.mock("@/lib/api/client", () => ({
  api: {
    getPackageRequiredCredentials: (name: string) =>
      getPackageRequiredCredentialsMock(name),
    getProviderCredentialStatus: (provider: string, agentImage?: string) =>
      getProviderCredentialStatusMock(provider, agentImage),
  },
  ApiError: class ApiError extends Error {},
}));

import {
  PackageCredentialRequirementsPanel,
  buildCredentialPayloadFromValues,
} from "./package-credential-requirements-panel";

function wrap(ui: ReactNode) {
  const client = new QueryClient({
    defaultOptions: {
      queries: { retry: false, gcTime: 0 },
    },
  });
  return <QueryClientProvider client={client}>{ui}</QueryClientProvider>;
}

beforeEach(() => {
  getPackageRequiredCredentialsMock.mockReset();
  getProviderCredentialStatusMock.mockReset();
});

describe("PackageCredentialRequirementsPanel", () => {
  it("renders nothing when no package is selected", () => {
    const { container } = render(
      wrap(
        <PackageCredentialRequirementsPanel
          packageName={null}
          values={{}}
          onValuesChange={vi.fn()}
        />,
      ),
    );
    expect(container).toBeEmptyDOMElement();
  });

  it("hides the panel when the package has no LLM requirements", async () => {
    getPackageRequiredCredentialsMock.mockResolvedValue({ required: [] });
    const { container } = render(
      wrap(
        <PackageCredentialRequirementsPanel
          packageName="ollama-only"
          values={{}}
          onValuesChange={vi.fn()}
        />,
      ),
    );
    await waitFor(() => {
      expect(getPackageRequiredCredentialsMock).toHaveBeenCalledWith(
        "ollama-only",
      );
    });
    // After the empty list resolves the panel renders nothing.
    await waitFor(() => {
      expect(
        screen.queryByTestId("package-credential-requirements"),
      ).not.toBeInTheDocument();
    });
    expect(container).toBeInTheDocument();
  });

  it("renders an input for each unsatisfied edge and forwards values upstream", async () => {
    getPackageRequiredCredentialsMock.mockResolvedValue({
      required: [
        {
          provider: "anthropic",
          authMethod: "oauth",
          secretName: "anthropic-oauth",
          credentialEnvVar: "CLAUDE_CODE_OAUTH_TOKEN",
          consumingUnits: ["alpha"],
        },
      ],
    });
    // Tenant doesn't satisfy → input renders.
    getProviderCredentialStatusMock.mockResolvedValue({ resolvable: false });

    const onValuesChange = vi.fn();
    render(
      wrap(
        <PackageCredentialRequirementsPanel
          packageName="oss"
          values={{}}
          onValuesChange={onValuesChange}
        />,
      ),
    );

    const input = await screen.findByTestId(
      "package-credential-input-anthropic-oauth",
    );
    fireEvent.change(input, { target: { value: "sk-ant-oat-xxx" } });
    expect(onValuesChange).toHaveBeenCalledWith({
      "anthropic:oauth": "sk-ant-oat-xxx",
    });
  });

  it("hides the input for an edge already satisfied at tenant scope", async () => {
    getPackageRequiredCredentialsMock.mockResolvedValue({
      required: [
        {
          provider: "anthropic",
          authMethod: "oauth",
          secretName: "anthropic-oauth",
          credentialEnvVar: "CLAUDE_CODE_OAUTH_TOKEN",
          consumingUnits: ["alpha"],
        },
      ],
    });
    getProviderCredentialStatusMock.mockResolvedValue({
      resolvable: true,
      source: "tenant",
    });

    render(
      wrap(
        <PackageCredentialRequirementsPanel
          packageName="oss"
          values={{}}
          onValuesChange={vi.fn()}
        />,
      ),
    );

    await screen.findByTestId(
      "package-credential-requirement-resolved-anthropic-oauth",
    );
    expect(
      screen.queryByTestId("package-credential-input-anthropic-oauth"),
    ).not.toBeInTheDocument();
  });
});

describe("buildCredentialPayloadFromValues", () => {
  const required = [
    {
      provider: "anthropic",
      authMethod: "oauth",
      secretName: "anthropic-oauth",
      credentialEnvVar: "CLAUDE_CODE_OAUTH_TOKEN",
      consumingUnits: ["alpha"],
    },
    {
      provider: "openai",
      authMethod: "api-key",
      secretName: "openai-api-key",
      credentialEnvVar: "OPENAI_API_KEY",
      consumingUnits: ["beta"],
    },
  ];

  it("emits one payload entry per non-empty value, in declaration order", () => {
    const payload = buildCredentialPayloadFromValues(required, {
      "anthropic:oauth": "sk-ant-oat-xxx",
      "openai:api-key": "sk-...",
    });
    expect(payload).toEqual([
      {
        provider: "anthropic",
        authMethod: "oauth",
        value: "sk-ant-oat-xxx",
      },
      {
        provider: "openai",
        authMethod: "api-key",
        value: "sk-...",
      },
    ]);
  });

  it("drops entries whose value is empty / whitespace", () => {
    const payload = buildCredentialPayloadFromValues(required, {
      "anthropic:oauth": "  ",
      "openai:api-key": "real-value",
    });
    expect(payload).toEqual([
      {
        provider: "openai",
        authMethod: "api-key",
        value: "real-value",
      },
    ]);
  });

  it("drops entries that don't appear in required[] (avoids UnknownCredentialEdge on retry)", () => {
    const payload = buildCredentialPayloadFromValues(
      [required[0]],
      {
        "anthropic:oauth": "sk-ant-oat-xxx",
        "openai:api-key": "sk-stale",
      },
    );
    expect(payload).toEqual([
      {
        provider: "anthropic",
        authMethod: "oauth",
        value: "sk-ant-oat-xxx",
      },
    ]);
  });
});
