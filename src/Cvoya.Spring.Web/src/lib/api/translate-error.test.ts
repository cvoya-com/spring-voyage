import { describe, expect, it } from "vitest";

import { ApiError } from "./client";
import { translateApiError } from "./translate-error";

describe("translateApiError", () => {
  it.each([
    [
      "ConnectorBindingMissing",
      {
        code: "ConnectorBindingMissing",
        missing: [{ slug: "github", scope: "package", unitName: null }],
        traceId: "00-trace",
      },
      "This package needs a github connector binding.",
      "Open the github step in the wizard and pick (or set up) a connector for the package.",
    ],
    [
      "PackageNotFound",
      {
        code: "PackageNotFound",
        detail: "package `spring-voyage-oss` was not found",
      },
      "Couldn't find package `spring-voyage-oss`.",
      "Run `spring package list` (or refresh the catalog) to confirm the package name and version.",
    ],
    [
      "UnitNotFound",
      { code: "UnitNotFound" },
      "Unit not found.",
      "It may have been deleted. Refresh the page or pick another unit.",
    ],
    [
      "AgentNotFound",
      { code: "AgentNotFound" },
      "Agent not found.",
      "It may have been deleted. Refresh the page or pick another agent.",
    ],
    [
      "LifecycleConflict",
      { code: "LifecycleConflict", action: "start", currentStatus: "Running" },
      "Can't start this unit while it's `Running`.",
      "Wait for the current operation to finish, then retry.",
    ],
    [
      "CredentialMissing",
      { code: "CredentialMissing", credentialEnvVar: "OPENAI_API_KEY" },
      "Required credential `OPENAI_API_KEY` isn't set.",
      "Set it in Config -> Secrets on this unit, on a parent unit, or on the tenant.",
    ],
    [
      "CredentialInvalid",
      { code: "CredentialInvalid", provider: "openai" },
      "The configured credential for `openai` was rejected by the provider.",
      "Check the secret value and try again.",
    ],
    [
      "ValidationFailed",
      { code: "ValidationFailed", detail: "Display name must not be empty." },
      "The request was invalid.",
      "Display name must not be empty.",
    ],
    [
      "InvalidState",
      {
        code: "InvalidState",
        currentStatus: "Draft",
        detail: "Unit is Draft; revalidation is only allowed from Error or Stopped.",
      },
      "Can't revalidate this unit while it's `Draft`.",
      "Wait for the current operation to finish, then retry.",
    ],
    [
      "ConfigurationIncomplete",
      {
        code: "ConfigurationIncomplete",
        missing: [{ unitName: "design", field: "execution.image" }],
      },
      "Package configuration for design is missing execution.image.",
      "Complete the missing configuration, then retry the install.",
    ],
    [
      "UnknownConnectorSlug",
      { code: "UnknownConnectorSlug", slug: "slack" },
      "This package doesn't declare a slack connector binding.",
      "Remove that connector binding or choose a connector required by this package.",
    ],
    [
      "MultiParentInheritanceConflict",
      { error: "MultiParentInheritanceConflict" },
      "Parent units disagree on inherited execution settings.",
      "Remove a conflicting parent or set the inherited field explicitly.",
    ],
  ])(
    "translates %s",
    (_code, problem, expectedTitle, expectedNextStep) => {
      const translated = translateApiError(
        new ApiError(400, "Bad Request", {
          type: "https://cvoya.com/problems/test",
          title: "Bad Request",
          status: 400,
          ...problem,
        }),
      );

      expect(translated.title).toBe(expectedTitle);
      expect(translated.nextStep).toBe(expectedNextStep);
      expect(translated.details?.raw).toMatch(/"(code|error)"/);
    },
  );

  it("falls back to the server title and detail for unknown codes", () => {
    const translated = translateApiError(
      new ApiError(418, "I'm a teapot", {
        type: "https://cvoya.com/problems/tea",
        title: "Tea service unavailable",
        status: 418,
        detail: "The kettle is empty.",
        code: "KettleEmpty",
        traceId: "00-tea",
      }),
    );

    expect(translated.title).toBe("Tea service unavailable");
    expect(translated.nextStep).toBe("The kettle is empty.");
    expect(translated.details?.traceId).toBe("00-tea");
    expect(translated.details?.raw).toContain("KettleEmpty");
  });

  it("falls back for non-ApiError values", () => {
    const translated = translateApiError(new Error("boom"));

    expect(translated.title).toBe("Something went wrong.");
    expect(translated.details?.raw).toBe("Error: boom");
  });
});
