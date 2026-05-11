import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { ApiError } from "@/lib/api/client";

import { ApiErrorMessage } from "./api-error-message";

describe("ApiErrorMessage", () => {
  it("renders translated copy and keeps raw ProblemDetails behind a disclosure", () => {
    render(
      <ApiErrorMessage
        error={
          new ApiError(400, "Bad Request", {
            type: "https://cvoya.com/problems/connector-binding-missing",
            title: "Bad Request",
            status: 400,
            detail:
              "ConnectorBindingMissing: package requires connector 'github'",
            code: "ConnectorBindingMissing",
            missing: [{ slug: "github", scope: "package", unitName: null }],
            traceId: "00-abc",
          })
        }
      />,
    );

    expect(
      screen.getByText("This package needs a github connector binding."),
    ).toBeInTheDocument();
    expect(
      screen.getByText(
        "Open the github step in the wizard and pick (or set up) a connector for the package.",
      ),
    ).toBeInTheDocument();
    expect(screen.queryByText(/API error 400/)).not.toBeInTheDocument();
    expect(screen.getByText("Show details")).toBeInTheDocument();
    expect(screen.getByText("00-abc")).toBeInTheDocument();
  });
});
