import type { ApiError, ParsedProblemDetails } from "./client";

export type TranslatedError = {
  /** One-sentence user-facing description of what went wrong. */
  title: string;
  /** Optional second sentence telling the user what to do next. */
  nextStep?: string;
  /** Render this as a small "Show details" disclosure containing traceId + raw envelope. */
  details?: { traceId?: string; raw?: string };
};

type ProblemTranslator = (problem: ParsedProblemDetails) => TranslatedError;

type MissingConnector = {
  slug?: string;
  scope?: string | null;
  unitName?: string | null;
};

const translators: Record<string, ProblemTranslator> = {
  ConnectorBindingMissing: translateConnectorBindingMissing,
  PackageNotFound: translatePackageNotFound,
  UnitNotFound: () => ({
    title: "Unit not found.",
    nextStep: "It may have been deleted. Refresh the page or pick another unit.",
  }),
  AgentNotFound: () => ({
    title: "Agent not found.",
    nextStep: "It may have been deleted. Refresh the page or pick another agent.",
  }),
  LifecycleConflict: translateLifecycleConflict,
  InvalidState: translateLifecycleConflict,
  CredentialMissing: translateCredentialMissing,
  CredentialInvalid: translateCredentialInvalid,
  ValidationFailed: (problem) => ({
    title: "The request was invalid.",
    nextStep: stringField(problem, "detail") ?? "Check the form for highlighted errors.",
  }),
  ConfigurationIncomplete: translateConfigurationIncomplete,
  UnknownConnectorSlug: translateUnknownConnectorSlug,
  MultiParentInheritanceConflict: (problem) => ({
    title: "Parent units disagree on inherited execution settings.",
    nextStep:
      stringField(problem, "detail") ??
      "Remove a conflicting parent or set the inherited field explicitly.",
  }),
  ImagePullFailed: translateImagePullFailed,
  ImageStartFailed: translateImageStartFailed,
  ToolMissing: translateToolMissing,
  CredentialFormatRejected: translateCredentialFormatRejected,
  ModelNotFound: translateModelNotFound,
  ProbeTimeout: () => ({
    title: "The runtime probe timed out.",
    nextStep:
      "Verify the agent host is responsive and retry; raise the probe timeout if this is expected.",
  }),
  ProbeInternalError: (problem) => ({
    title: "The runtime probe failed unexpectedly.",
    nextStep:
      stringField(problem, "detail") ??
      "Check the host logs (`spring agent logs <id>` or `kubectl logs`) and retry.",
  }),
};

export function translateApiError(err: unknown): TranslatedError {
  const apiError = asApiError(err);
  if (apiError) {
    const problem = apiError.problem ?? parseProblemDetails(apiError.body);
    const code = problem?.code ?? stringField(problem, "error");
    const translated =
      problem && code ? translators[code]?.(problem) : undefined;

    return withDetails(
      translated ?? translateUnknownApiError(problem, apiError.body),
      problem,
      apiError.body,
    );
  }

  return {
    title: "Something went wrong.",
    details: { raw: String(err) },
  };
}

function asApiError(
  err: unknown,
): Pick<ApiError, "body" | "problem"> | undefined {
  if (!isRecord(err)) {
    return undefined;
  }

  return typeof err.status === "number" && "body" in err
    ? (err as Pick<ApiError, "body" | "problem">)
    : undefined;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function parseProblemDetails(
  body: unknown,
): ParsedProblemDetails | undefined {
  if (!isRecord(body)) {
    return undefined;
  }

  const type = typeof body.type === "string" ? body.type : undefined;
  const title = typeof body.title === "string" ? body.title : undefined;
  const detail = typeof body.detail === "string" ? body.detail : undefined;
  const code = typeof body.code === "string" ? body.code : undefined;
  const traceId =
    typeof body.traceId === "string" ? body.traceId : undefined;
  const status = typeof body.status === "number" ? body.status : undefined;

  if (
    type === undefined &&
    code === undefined &&
    !(title !== undefined && status !== undefined)
  ) {
    return undefined;
  }

  return {
    ...body,
    ...(type !== undefined ? { type } : {}),
    ...(title !== undefined ? { title } : {}),
    ...(status !== undefined ? { status } : {}),
    ...(detail !== undefined ? { detail } : {}),
    ...(code !== undefined ? { code } : {}),
    ...(traceId !== undefined ? { traceId } : {}),
  };
}

export function formatTranslatedError(err: unknown): string {
  const translated = translateApiError(err);
  return translated.nextStep
    ? `${translated.title} ${translated.nextStep}`
    : translated.title;
}

function translateConnectorBindingMissing(
  problem: ParsedProblemDetails,
): TranslatedError {
  const missing = firstMissingConnector(problem);
  const slug = missing?.slug ?? "required";
  const target = missing?.unitName?.trim() || "the package";
  return {
    title: `This package needs a ${slug} connector binding.`,
    nextStep: `Open the ${slug} step in the wizard and pick (or set up) a connector for ${target}.`,
  };
}

function translatePackageNotFound(problem: ParsedProblemDetails): TranslatedError {
  const packageName = packageNameFromProblem(problem);
  return {
    title: packageName
      ? `Couldn't find package \`${packageName}\`.`
      : "Couldn't find that package.",
    nextStep:
      "Run `spring package list` (or refresh the catalog) to confirm the package name and version.",
  };
}

function translateLifecycleConflict(problem: ParsedProblemDetails): TranslatedError {
  const action =
    stringField(problem, "action") ??
    actionFromDetail(problem.detail) ??
    "update";
  const currentState =
    stringField(problem, "currentStatus") ??
    stringField(problem, "currentState") ??
    stringField(problem, "state");
  const hint =
    stringField(problem, "hint") ??
    stringField(problem, "forceHint") ??
    stringField(problem, "next");

  return {
    title: currentState
      ? `Can't ${action} this unit while it's \`${currentState}\`.`
      : `Can't ${action} this unit right now.`,
    nextStep: hint ?? "Wait for the current operation to finish, then retry.",
  };
}

function translateCredentialMissing(problem: ParsedProblemDetails): TranslatedError {
  const credential =
    stringField(problem, "credentialEnvVar") ??
    stringField(problem, "credential") ??
    stringField(problem, "secretName") ??
    "the required credential";
  return {
    title: `Required credential \`${credential}\` isn't set.`,
    nextStep:
      "Set it in Config -> Secrets on this unit, on a parent unit, or on the tenant.",
  };
}

function translateCredentialInvalid(problem: ParsedProblemDetails): TranslatedError {
  const provider =
    stringField(problem, "provider") ??
    stringField(problem, "modelProvider") ??
    "this provider";
  return {
    title: `The configured credential for \`${provider}\` was rejected by the provider.`,
    nextStep: "Check the secret value and try again.",
  };
}

function translateConfigurationIncomplete(
  problem: ParsedProblemDetails,
): TranslatedError {
  const missing = Array.isArray(problem.missing) ? problem.missing[0] : null;
  const unitName = fieldFromRecord(missing, "unitName");
  const field = fieldFromRecord(missing, "field");
  return {
    title: unitName && field
      ? `Package configuration for ${unitName} is missing ${field}.`
      : "This package is missing required configuration.",
    nextStep:
      stringField(problem, "detail") ??
      "Complete the missing configuration, then retry the install.",
  };
}

function translateUnknownConnectorSlug(
  problem: ParsedProblemDetails,
): TranslatedError {
  const slug = stringField(problem, "slug") ?? "that";
  return {
    title: `This package doesn't declare a ${slug} connector binding.`,
    nextStep:
      "Remove that connector binding or choose a connector required by this package.",
  };
}

function translateImagePullFailed(problem: ParsedProblemDetails): TranslatedError {
  return {
    title: "Couldn't pull the agent image.",
    nextStep:
      stringField(problem, "detail") ??
      "Check that the image exists and the host can reach the registry.",
  };
}

function translateImageStartFailed(problem: ParsedProblemDetails): TranslatedError {
  return {
    title: "Couldn't start the agent container.",
    nextStep:
      stringField(problem, "detail") ??
      "Check the agent image and host runtime logs.",
  };
}

function translateToolMissing(problem: ParsedProblemDetails): TranslatedError {
  const tool = stringField(problem, "tool") ?? "required";
  return {
    title: `The agent image is missing the ${tool} CLI.`,
    nextStep: "Pick a different agent image or install the CLI before retrying.",
  };
}

function translateCredentialFormatRejected(
  problem: ParsedProblemDetails,
): TranslatedError {
  const provider =
    stringField(problem, "provider") ??
    stringField(problem, "modelProvider") ??
    "this provider";
  return {
    title: `The configured credential's format isn't accepted by ${provider}.`,
    nextStep:
      stringField(problem, "detail") ??
      "Update the secret to a value of the right shape (see the provider's docs).",
  };
}

function translateModelNotFound(problem: ParsedProblemDetails): TranslatedError {
  const model = stringField(problem, "model") ?? "(unknown)";
  const provider =
    stringField(problem, "provider") ??
    stringField(problem, "modelProvider") ??
    "this provider";
  return {
    title: `Model \`${model}\` isn't available for ${provider}.`,
    nextStep: "Pick a model from the provider's catalogue or update the install.",
  };
}

function translateUnknownApiError(
  problem: ParsedProblemDetails | undefined,
  body: unknown,
): TranslatedError {
  return {
    title: problem?.title ?? "Couldn't complete the request.",
    nextStep: problem?.detail,
    details: { traceId: problem?.traceId, raw: stringify(body) },
  };
}

function withDetails(
  translated: TranslatedError,
  problem: ParsedProblemDetails | undefined,
  body: unknown,
): TranslatedError {
  return {
    ...translated,
    details: {
      traceId: translated.details?.traceId ?? problem?.traceId,
      raw: translated.details?.raw ?? stringify(body),
    },
  };
}

function firstMissingConnector(
  problem: ParsedProblemDetails,
): MissingConnector | null {
  const missing = problem.missing;
  if (!Array.isArray(missing) || missing.length === 0) {
    return null;
  }
  const first = missing[0];
  if (!first || typeof first !== "object") {
    return null;
  }
  return first as MissingConnector;
}

function packageNameFromProblem(
  problem: ParsedProblemDetails,
): string | undefined {
  const explicit =
    stringField(problem, "packageName") ??
    stringField(problem, "name") ??
    stringField(problem, "package");
  if (explicit) {
    return explicit;
  }
  const detail = problem.detail ?? "";
  return (
    /package [`'"]([^`'"]+)[`'"]/i.exec(detail)?.[1] ??
    /package ([^\s.,;]+)/i.exec(detail)?.[1]
  );
}

function actionFromDetail(detail: string | undefined): string | undefined {
  if (!detail) {
    return undefined;
  }
  const lower = detail.toLowerCase();
  if (lower.includes("revalidation") || lower.includes("revalidate")) {
    return "revalidate";
  }
  if (lower.includes("start")) {
    return "start";
  }
  if (lower.includes("stop")) {
    return "stop";
  }
  if (lower.includes("delete") || lower.includes("deleting")) {
    return "delete";
  }
  return undefined;
}

function stringField(
  record: Record<string, unknown> | undefined,
  key: string,
): string | undefined {
  const value = record?.[key];
  return typeof value === "string" && value.trim().length > 0
    ? value
    : undefined;
}

function fieldFromRecord(value: unknown, key: string): string | undefined {
  if (!value || typeof value !== "object") {
    return undefined;
  }
  const field = (value as Record<string, unknown>)[key];
  return typeof field === "string" && field.trim().length > 0
    ? field
    : undefined;
}

function stringify(value: unknown): string {
  if (typeof value === "string") {
    return value;
  }
  if (value instanceof Error) {
    return value.message;
  }
  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}
