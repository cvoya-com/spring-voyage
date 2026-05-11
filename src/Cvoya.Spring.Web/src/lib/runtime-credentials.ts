import {
  RUNTIMES,
  credentialSecretNameFor,
  getRuntimeProviderCredentialEdge,
  type AuthMethod,
  type ProviderId,
  type RuntimeId,
} from "@/lib/ai-models";

export interface CredentialHelpLink {
  href: string;
  label: string;
}

export interface RuntimeCredentialDescriptor {
  runtimeId: RuntimeId;
  providerId: ProviderId;
  authMethod: AuthMethod;
  credentialEnvVar: string;
  secretName: string;
  label: string;
  placeholder: string;
  helpText: string;
  helpLink: CredentialHelpLink;
}

const API_KEY_HELP: Readonly<
  Record<"anthropic" | "openai" | "google", CredentialHelpLink>
> = {
  anthropic: {
    href: "https://console.anthropic.com/settings/keys",
    label: "Get an Anthropic API key",
  },
  openai: {
    href: "https://platform.openai.com/api-keys",
    label: "Get an OpenAI API key",
  },
  google: {
    href: "https://aistudio.google.com/app/apikey",
    label: "Get a Google AI API key",
  },
};

const CLAUDE_CODE_TOKEN_HELP: CredentialHelpLink = {
  href: "https://code.claude.com/docs/en/authentication#generate-a-long-lived-token",
  label: "How to get a Claude Code token",
};

export function runtimeCredentialDescriptor(
  runtimeId: string | null,
  providerId: string | null,
): RuntimeCredentialDescriptor | null {
  const edge = getRuntimeProviderCredentialEdge(runtimeId, providerId);
  if (!edge?.authMethod || !edge.credentialEnvVar) return null;

  const runtime = RUNTIMES[runtimeId as RuntimeId];
  if (!runtime) return null;

  const provider = edge.providerId;
  const secretName = credentialSecretNameFor(provider, edge.authMethod);

  if (runtime.id === "claude-code" && provider === "anthropic") {
    return {
      runtimeId: runtime.id,
      providerId: provider,
      authMethod: "oauth",
      credentialEnvVar: edge.credentialEnvVar,
      secretName,
      label: "Claude Code OAuth token",
      placeholder: "Paste the OAuth token from claude setup-token",
      helpText:
        "Run `claude setup-token` and paste the generated OAuth token. Spring Voyage stores it as `anthropic-oauth` and injects it as `CLAUDE_CODE_OAUTH_TOKEN`.",
      helpLink: CLAUDE_CODE_TOKEN_HELP,
    };
  }

  const providerName = providerDisplayName(provider);
  const placeholder =
    provider === "anthropic"
      ? "Paste your Anthropic API key (sk-ant-api...)"
      : `Paste your ${providerName} API key`;
  const helpLink =
    provider === "anthropic" || provider === "openai" || provider === "google"
      ? API_KEY_HELP[provider]
      : {
          href: "https://docs.cvoya.com/",
          label: `Get a ${providerName} API key`,
        };

  return {
    runtimeId: runtime.id,
    providerId: provider,
    authMethod: edge.authMethod,
    credentialEnvVar: edge.credentialEnvVar,
    secretName,
    label: `${providerName} API key`,
    placeholder,
    helpText: `Spring Voyage stores this as \`${secretName}\` and injects it as \`${edge.credentialEnvVar}\`.`,
    helpLink,
  };
}

export function providerDisplayName(providerId: string): string {
  switch (providerId) {
    case "anthropic":
      return "Anthropic";
    case "openai":
      return "OpenAI";
    case "google":
    case "gemini":
    case "googleai":
      return "Google";
    case "ollama":
      return "Ollama";
    default:
      return providerId;
  }
}
