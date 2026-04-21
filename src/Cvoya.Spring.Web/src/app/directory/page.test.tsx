/**
 * Unit tests for the `/directory` tenant-wide expertise surface
 * (#486 / #542). The page now rides the backend search endpoint, so
 * these tests mock `api.searchDirectory` to exercise the rendering,
 * typed-only filter, and empty state.
 */

import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { ReactNode } from "react";

import type {
  DirectorySearchRequest,
  DirectorySearchResponse,
} from "@/lib/api/types";

const searchDirectory =
  vi.fn<(body: DirectorySearchRequest) => Promise<DirectorySearchResponse>>();

vi.mock("@/lib/api/client", () => ({
  api: {
    searchDirectory: (body: DirectorySearchRequest) => searchDirectory(body),
  },
}));

vi.mock("next/link", () => ({
  default: ({
    href,
    children,
    ...rest
  }: {
    href: string;
    children: ReactNode;
  } & Record<string, unknown>) => (
    <a href={href} {...rest}>
      {children}
    </a>
  ),
}));

import DirectoryPage from "./page";

function makeResponse(
  overrides: Partial<DirectorySearchResponse> = {},
): DirectorySearchResponse {
  return {
    hits: [],
    totalCount: 0,
    limit: 50,
    offset: 0,
    ...overrides,
  } as DirectorySearchResponse;
}

function renderPage() {
  const client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  });
  return render(
    <QueryClientProvider client={client}>
      <DirectoryPage />
    </QueryClientProvider>,
  );
}

describe("/directory", () => {
  beforeEach(() => {
    searchDirectory.mockReset();
  });

  it("renders the empty state when the search returns no hits", async () => {
    searchDirectory.mockResolvedValue(makeResponse());

    renderPage();

    await waitFor(() => {
      expect(screen.getByText(/No results/i)).toBeInTheDocument();
    });
  });

  it("renders hits returned by the search endpoint", async () => {
    searchDirectory.mockResolvedValue(
      makeResponse({
        totalCount: 2,
        hits: [
          {
            slug: "python",
            domain: { name: "python", description: "", level: "expert" },
            owner: { scheme: "agent", path: "ada" },
            ownerDisplayName: "Ada",
            aggregatingUnit: null,
            ancestorChain: [],
            projectionPaths: [],
            typedContract: true,
            score: 100,
            matchReason: "exact slug",
          },
          {
            slug: "team-coordination",
            domain: {
              name: "team-coordination",
              description: "",
              level: null,
            },
            owner: { scheme: "unit", path: "engineering" },
            ownerDisplayName: "Engineering",
            aggregatingUnit: null,
            ancestorChain: [],
            projectionPaths: [],
            typedContract: false,
            score: 30,
            matchReason: "no text",
          },
        ],
      }),
    );

    renderPage();

    await waitFor(() => {
      // The slug is rendered as <code>python</code> and the domain name
      // as a separate span — assert through getAllByText so we tolerate
      // both occurrences without being brittle about which element wins.
      expect(screen.getAllByText("python").length).toBeGreaterThan(0);
      expect(
        screen.getAllByText("team-coordination").length,
      ).toBeGreaterThan(0);
    });

    // Each row deep-links to the owning agent or unit page.
    const agentLink = screen.getByRole("link", { name: /agent:\/\/ada/i });
    expect(agentLink).toHaveAttribute("href", "/agents/ada");
    const unitLink = screen.getByRole("link", {
      name: /unit:\/\/engineering/i,
    });
    expect(unitLink).toHaveAttribute("href", "/units/engineering");
  });

  it("submits the text query when the user hits Enter", async () => {
    searchDirectory.mockResolvedValue(makeResponse());
    renderPage();

    await waitFor(() => {
      expect(searchDirectory).toHaveBeenCalled();
    });

    fireEvent.change(screen.getByLabelText(/Search expertise/i), {
      target: { value: "python" },
    });
    fireEvent.keyDown(screen.getByLabelText(/Search expertise/i), {
      key: "Enter",
      code: "Enter",
    });

    await waitFor(() => {
      const last =
        searchDirectory.mock.calls[searchDirectory.mock.calls.length - 1];
      expect(last?.[0]?.text).toBe("python");
    });
  });

  it("sends typedOnly when the filter is enabled", async () => {
    searchDirectory.mockResolvedValue(makeResponse());
    renderPage();

    await waitFor(() => {
      expect(searchDirectory).toHaveBeenCalled();
    });

    fireEvent.click(screen.getByLabelText(/Typed contract only/i));

    await waitFor(() => {
      const last =
        searchDirectory.mock.calls[searchDirectory.mock.calls.length - 1];
      expect(last?.[0]?.typedOnly).toBe(true);
    });
  });
});
