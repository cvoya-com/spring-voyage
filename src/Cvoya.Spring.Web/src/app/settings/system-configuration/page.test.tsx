import { render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import SettingsSystemConfigurationPage from "./page";

describe("SettingsSystemConfigurationPage", () => {
  const originalFetch = globalThis.fetch;

  beforeEach(() => {
    globalThis.fetch = vi.fn(
      async () =>
        new Response(
          JSON.stringify({
            status: "Healthy",
            generatedAt: "2026-04-18T10:00:00Z",
            subsystems: [],
          }),
          {
            status: 200,
            headers: { "Content-Type": "application/json" },
          },
        ),
    ) as unknown as typeof fetch;
  });

  afterEach(() => {
    globalThis.fetch = originalFetch;
    vi.restoreAllMocks();
  });

  it("renders the h1 landmark (re-exported from /system/configuration)", async () => {
    render(<SettingsSystemConfigurationPage />);
    await waitFor(() => {
      expect(
        screen.getByRole("heading", {
          level: 1,
          name: /system configuration/i,
        }),
      ).toBeInTheDocument();
    });
  });
});
