// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.ErrorHandling;
using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

using Microsoft.Kiota.Abstractions;

/// <summary>
/// Builds the "agent" command tree for agent management.
/// </summary>
public static class AgentCommand
{
    private static readonly OutputFormatter.Column<AgentResponse>[] AgentListColumns =
    {
        new("id", a => GuidDisplay.Format(a.Id)),
        new("name", a => a.Name),
        new("role", a => a.Role),
        new("enabled", a => a.Enabled?.ToString().ToLowerInvariant()),
        new("hosting", a => a.HostingMode),
        new("initiative", a => a.InitiativeLevel),
    };

    private static readonly OutputFormatter.Column<AgentResponse>[] AgentCreateColumns =
    {
        new("id", a => GuidDisplay.Format(a.Id)),
        new("name", a => a.Name),
        new("role", a => a.Role),
    };

    private static readonly OutputFormatter.Column<AgentDetailResponse>[] AgentStatusColumns =
    {
        new("id", a => GuidDisplay.Format(a.Agent?.Id)),
        new("name", a => a.Agent?.Name),
        new("enabled", a => a.Agent?.Enabled?.ToString().ToLowerInvariant()),
        // Persistent-agent enrichment (#396). Kiota models the nullable
        // deployment slot as a composed oneOf; the typed branch is the
        // PersistentAgentDeploymentResponse member. When the agent is not
        // deployed the slot is null and these columns stay blank.
        new("running", a => a.Deployment?.PersistentAgentDeploymentResponse?.Running?.ToString().ToLowerInvariant()),
        new("health", a => a.Deployment?.PersistentAgentDeploymentResponse?.HealthStatus),
        new("container", a => a.Deployment?.PersistentAgentDeploymentResponse?.ContainerId),
    };

    private static readonly OutputFormatter.Column<PersistentAgentDeploymentResponse>[] DeploymentColumns =
    {
        new("agentId", d => d.AgentId),
        new("running", d => d.Running?.ToString().ToLowerInvariant()),
        new("health", d => d.HealthStatus),
        new("replicas", d => d.Replicas?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty),
        new("image", d => d.Image),
        new("endpoint", d => d.Endpoint),
        new("container", d => d.ContainerId),
        new("startedAt", d => d.StartedAt is DateTimeOffset dto
            ? dto.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
            : null),
    };

    private sealed record CloneRow(
        string CloneId,
        string Parent,
        string CloneType,
        string AttachmentMode,
        string Status,
        string CreatedAt,
        string? LocalAlias);

    private static readonly OutputFormatter.Column<CloneRow>[] CloneColumns =
    {
        new("cloneId", r => r.CloneId),
        new("parent", r => r.Parent),
        new("cloneType", r => r.CloneType),
        new("attachmentMode", r => r.AttachmentMode),
        new("status", r => r.Status),
        new("createdAt", r => r.CreatedAt),
        new("alias", r => r.LocalAlias),
    };

    /// <summary>
    /// Creates the "agent" command with subcommands for CRUD operations,
    /// the cascading purge helper (#320), and the clone surface (#458).
    /// </summary>
    public static Command Create(Option<string> outputOption)
        => Create(outputOption, () => ClientFactory.Create());

    internal static Command Create(Option<string> outputOption, Func<SpringApiClient> clientFactory)
    {
        var agentCommand = new Command("agent", "Manage agents");

        agentCommand.Subcommands.Add(CreateListCommand(outputOption));
        agentCommand.Subcommands.Add(CreateCreateCommand(outputOption, clientFactory));
        // #1629 PR6 — `show <id-or-name>` accepts a Guid (canonical no-dash
        // 32-hex or dashed) for direct lookup, OR a display_name for search
        // with disambiguation. `--unit <name-or-guid>` constrains the search
        // to that unit's membership. Distinct from `status`, which returns
        // the actor's runtime + persistent-deployment state.
        agentCommand.Subcommands.Add(CreateShowCommand(outputOption));
        agentCommand.Subcommands.Add(CreateStatusCommand(outputOption));
        agentCommand.Subcommands.Add(CreateDeleteCommand());
        agentCommand.Subcommands.Add(CreatePurgeCommand());
        agentCommand.Subcommands.Add(CreateCloneCommand(outputOption));
        agentCommand.Subcommands.Add(ExpertiseCommand.CreateAgentSubcommand(outputOption));

        // Persistent-agent lifecycle verbs (#396). Together with the extended
        // `status` verb above, these close the CLI gap for operators managing
        // long-lived agent services. Ephemeral agents are unaffected — the
        // server returns a 400 if you try to deploy one.
        agentCommand.Subcommands.Add(CreateDeployCommand(outputOption));
        agentCommand.Subcommands.Add(CreateUndeployCommand(outputOption));
        agentCommand.Subcommands.Add(CreateScaleCommand(outputOption));
        agentCommand.Subcommands.Add(CreateLogsCommand());

        // #601 / #603 / #409 B-wide — execution get/set/clear for the
        // agent's own execution block on AgentDefinitions.Definition.
        // Unit defaults merge at dispatch; this verb edits the on-disk
        // agent slot only.
        agentCommand.Subcommands.Add(AgentExecutionCommand.Create(outputOption));
        // #2160: open operational issues against this agent.
        agentCommand.Subcommands.Add(IssuesCommand.CreateAgentSubcommand(outputOption));

        // #1377: `spring agent health <id>` — read the health status of a
        // persistent agent's backing container from the deployment endpoint.
        agentCommand.Subcommands.Add(CreateHealthCommand(outputOption));

        // #2096 / ADR-0041: `spring agent validate <id>` — surface
        // author-facing warnings about runtime-contract opt-ins
        // (e.g. concurrent_threads: true). Non-blocking — exit 0 unless
        // lookup fails.
        agentCommand.Subcommands.Add(AgentValidateCommand.Create(outputOption, clientFactory));

        return agentCommand;
    }

    /// <summary>Allowed values for the <c>--hosting</c> filter flag (#572).</summary>
    public static readonly string[] HostingKeys = ["ephemeral", "persistent"];

    /// <summary>Allowed values for the <c>--initiative</c> filter flag (#573).</summary>
    public static readonly string[] InitiativeKeys = ["passive", "attentive", "proactive", "autonomous"];

    private static Command CreateListCommand(Option<string> outputOption)
    {
        // #572: --hosting filters by the agent's declared hosting mode.
        var hostingOption = new Option<string?>("--hosting")
        {
            Description = "Filter agents by hosting mode. Allowed: " + string.Join(", ", HostingKeys) + ".",
        };
        hostingOption.AcceptOnlyFromAmong(HostingKeys);

        // #573: --initiative filters by the agent's effective initiative level.
        // Multi-valued: repeat the flag or comma-separate to match multiple
        // levels (e.g. --initiative proactive --initiative autonomous).
        var initiativeOption = new Option<string[]>("--initiative")
        {
            Description = "Filter agents by initiative level. Allowed: " + string.Join(", ", InitiativeKeys) + ". " +
                "Repeat the flag or comma-separate to include multiple levels.",
            AllowMultipleArgumentsPerToken = true,
        };
        initiativeOption.AcceptOnlyFromAmong(InitiativeKeys);

        var command = new Command("list", "List all agents");
        command.Options.Add(hostingOption);
        command.Options.Add(initiativeOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var output = parseResult.GetValue(outputOption) ?? "table";
            var hosting = parseResult.GetValue(hostingOption);
            var initiative = parseResult.GetValue(initiativeOption) ?? [];

            var client = ClientFactory.Create();

            // #1402: pass filter params to the server so the API does the
            // filtering rather than the CLI fetching the full list. The CLI
            // also applies the same filter client-side as a defensive fallback
            // in case the CLI is talking to an older API version that ignores
            // the query parameters and returns the full list.
            var result = await client.ListAgentsAsync(
                hosting: hosting,
                initiative: initiative.Length > 0 ? initiative : null,
                ct: ct);

            // Defensive client-side filter fallback: if the server returned
            // agents that don't match the requested filter (e.g. an older API
            // version that ignores the query params), filter them out here so
            // the output is always consistent with what the operator asked for.
            var filtered = result.AsEnumerable();

            if (!string.IsNullOrEmpty(hosting))
            {
                filtered = filtered.Where(a =>
                    string.Equals(a.HostingMode, hosting, StringComparison.OrdinalIgnoreCase));
            }

            if (initiative.Length > 0)
            {
                var initiativeSet = new HashSet<string>(initiative, StringComparer.OrdinalIgnoreCase);
                filtered = filtered.Where(a =>
                    a.InitiativeLevel is not null && initiativeSet.Contains(a.InitiativeLevel));
            }

            var list = filtered.ToList();

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(list)
                : OutputFormatter.FormatTable(list, AgentListColumns));
        });

        return command;
    }

    private static Command CreateCreateCommand(Option<string> outputOption, Func<SpringApiClient> clientFactory)
    {
        var nameOption = new Option<string?>("--name")
        {
            Description = "Human-readable display name. Required — agent identity is assigned by the platform (ADR-0039 §8).",
            Required = true,
        };
        var descriptionOption = new Option<string?>("--description")
        {
            Description = "Optional human-readable description for the agent.",
        };
        var roleOption = new Option<string?>("--role") { Description = "The agent role" };
        // ADR-0039: --unit is repeatable and optional. Omitting it creates a
        // top-level tenant-parented agent that inherits from tenant defaults.
        var unitOption = new Option<string[]>("--unit")
        {
            Description = "Unit to add the agent to. Repeat to assign multiple units; omit to create a top-level tenant-parented agent.",
            AllowMultipleArgumentsPerToken = true,
            Required = false,
        };
        var definitionFileOption = new Option<string?>("--definition-file")
        {
            Description =
                "Path to a JSON file containing the agent definition document (e.g. execution.tool/image/provider/model). " +
                "When supplied, its contents are sent verbatim to the server and persisted on AgentDefinitions.Definition.",
        };
        var definitionOption = new Option<string?>("--definition")
        {
            Description = "Inline JSON literal for the agent definition document. Alternative to --definition-file.",
        };
        var fromPackageOption = new Option<string?>("--from-package")
        {
            Description = "Install an agent from a package in the catalog. " +
                "Provide the package name. Mutually exclusive with --inherit, --definition, " +
                "--definition-file, and execution shorthand flags (full validation in L6).",
        };
        var connectorOption = new Option<string[]>("--connector")
        {
            Description = "Connector binding for the package install. Format: <slug>=<binding-id> or <slug>. " +
                "Repeat to bind multiple connectors. Only valid with --from-package; errors when used alone.",
            AllowMultipleArgumentsPerToken = true,
        };
        var inputOption = new Option<string[]>("--input")
        {
            Description = "Package install input (legacy). Format: <key>=<value>. " +
                "Repeat to set multiple inputs. Only valid with --from-package; errors when used alone.",
            AllowMultipleArgumentsPerToken = true,
        };

        // #409 acceptance: CLI parity for the agent execution block.
        // These flags are convenience shorthands for the equivalent
        // `execution.X` keys in --definition-file / --definition. When
        // BOTH a definition JSON and these flags are supplied, the
        // flags override the definition document (last-writer-wins per
        // field).
        var imageOption = new Option<string?>("--image")
        {
            Description = "Container image reference (shorthand for execution.image on the agent definition).",
        };

        // ADR-0038: agent-runtime selection is `--runtime <id>` (e.g.
        // claude-code, codex, gemini, spring-voyage). The structured model
        // pair `--model-provider` + `--model` ships the {provider, id} half
        // of the new wire shape. Provider is required only when the chosen
        // runtime accepts more than one provider; for fixed-provider
        // runtimes the flag MAY be supplied but its value MUST equal the
        // implied provider — the dispatcher rejects mismatches with a
        // precise error.
        var runtimeOption = new Option<string?>("--runtime")
        {
            Description = "Agent runtime id (shorthand for execution.runtime; e.g. claude-code, codex, gemini, spring-voyage).",
        };
        var modelProviderOption = new Option<string?>("--model-provider")
        {
            Description = "Model-provider id (shorthand for execution.model.provider; e.g. anthropic, openai, google, ollama). " +
                "Required for multi-provider runtimes (spring-voyage); optional for fixed-provider runtimes (must match the implied provider when supplied).",
        };
        var modelOption = new Option<string?>("--model")
        {
            Description = "Model id (shorthand for execution.model.id; e.g. claude-opus-4-7).",
        };
        var hostingOption = new Option<string?>("--hosting")
        {
            Description = "Hosting mode (shorthand for execution.hosting). Allowed values: " +
                string.Join(", ", AgentExecutionCommand.HostingKeys) + ".",
        };
        hostingOption.AcceptOnlyFromAmong(AgentExecutionCommand.HostingKeys);
        var inheritOption = new Option<bool>("--inherit")
        {
            Description = "Omit all execution fields; the agent inherits its runtime, model, image, and hosting from its parent unit(s).",
        };

        var command = new Command("create", "Create a new agent");
        command.Options.Add(nameOption);
        command.Options.Add(descriptionOption);
        command.Options.Add(roleOption);
        command.Options.Add(unitOption);
        command.Options.Add(definitionFileOption);
        command.Options.Add(definitionOption);
        command.Options.Add(fromPackageOption);
        command.Options.Add(connectorOption);
        command.Options.Add(inputOption);
        command.Options.Add(imageOption);
        command.Options.Add(runtimeOption);
        command.Options.Add(modelProviderOption);
        command.Options.Add(modelOption);
        command.Options.Add(hostingOption);
        command.Options.Add(inheritOption);

        command.Validators.Add(result =>
        {
            var fromPackage = result.GetValue(fromPackageOption);
            var connectors = result.GetValue(connectorOption) ?? Array.Empty<string>();
            var inputs = result.GetValue(inputOption) ?? Array.Empty<string>();
            var definitionFile = result.GetValue(definitionFileOption);
            var definitionInline = result.GetValue(definitionOption);
            var image = result.GetValue(imageOption);
            var runtime = result.GetValue(runtimeOption);
            var modelProvider = result.GetValue(modelProviderOption);
            var model = result.GetValue(modelOption);
            var hosting = result.GetValue(hostingOption);
            var inherit = result.GetValue(inheritOption);

            if (connectors.Length > 0 && string.IsNullOrWhiteSpace(fromPackage))
            {
                result.AddError(ConnectorRequiresFromPackageFlagMutexMessage);
                return;
            }

            if (inputs.Length > 0 && string.IsNullOrWhiteSpace(fromPackage))
            {
                result.AddError(InputRequiresFromPackageFlagMutexMessage);
                return;
            }

            if (!string.IsNullOrWhiteSpace(fromPackage) && inherit)
            {
                result.AddError(FromPackageInheritFlagMutexMessage);
                return;
            }

            if (!string.IsNullOrWhiteSpace(fromPackage)
                && (!string.IsNullOrWhiteSpace(definitionInline)
                    || !string.IsNullOrWhiteSpace(definitionFile)))
            {
                result.AddError(FromPackageDefinitionFlagMutexMessage);
                return;
            }

            if (inherit
                && (!string.IsNullOrWhiteSpace(definitionInline)
                    || !string.IsNullOrWhiteSpace(definitionFile)))
            {
                result.AddError(InheritDefinitionFlagMutexMessage);
                return;
            }

            var executionShorthandFlags = GetExecutionShorthandFlags(image, runtime, modelProvider, model, hosting);
            if (!string.IsNullOrWhiteSpace(fromPackage)
                && executionShorthandFlags.Count > 0)
            {
                result.AddError(FormatFromPackageExecutionShorthandFlagMutexMessage(executionShorthandFlags));
                return;
            }

            if (inherit && executionShorthandFlags.Count > 0)
            {
                result.AddError(FormatInheritExecutionShorthandFlagMutexMessage(executionShorthandFlags));
            }
        });

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            // ADR-0039 §8: identity is server-allocated. --name is the only
            // display surface and Required=true above guarantees the parser
            // already rejected an empty invocation with a clear hint.
            var displayName = parseResult.GetValue(nameOption)!;
            var description = parseResult.GetValue(descriptionOption);
            var role = parseResult.GetValue(roleOption);
            var units = parseResult.GetValue(unitOption) ?? Array.Empty<string>();
            var definitionFile = parseResult.GetValue(definitionFileOption);
            var definitionInline = parseResult.GetValue(definitionOption);
            var fromPackage = parseResult.GetValue(fromPackageOption);
            var connectors = parseResult.GetValue(connectorOption) ?? Array.Empty<string>();
            var inputs = parseResult.GetValue(inputOption) ?? Array.Empty<string>();
            var image = parseResult.GetValue(imageOption);
            var runtime = parseResult.GetValue(runtimeOption);
            var modelProvider = parseResult.GetValue(modelProviderOption);
            var model = parseResult.GetValue(modelOption);
            var hosting = parseResult.GetValue(hostingOption);
            var inherit = parseResult.GetValue(inheritOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (!string.IsNullOrWhiteSpace(fromPackage))
            {
                IReadOnlyDictionary<string, string>? packageInputs;
                try
                {
                    packageInputs = ParsePackageInputs(inputs);
                }
                catch (ArgumentException ex)
                {
                    await Console.Error.WriteLineAsync(ex.Message);
                    Environment.Exit(1);
                    return;
                }

                IReadOnlyDictionary<string, SpringApiClient.ConnectorBindingPayloadRequest>? packageConnectorBindings;
                try
                {
                    packageConnectorBindings = ParsePackageConnectorBindings(connectors);
                }
                catch (ArgumentException ex)
                {
                    await Console.Error.WriteLineAsync(ex.Message);
                    Environment.Exit(1);
                    return;
                }

                var connectorBindings = packageConnectorBindings is null
                    ? null
                    : new SpringApiClient.PackageConnectorBindingsRequest(packageConnectorBindings, Units: null);

                var installResult = await clientFactory().InstallPackagesAsync(
                    new[]
                    {
                        new SpringApiClient.PackageInstallTargetRequest(
                            fromPackage,
                            packageInputs,
                            connectorBindings),
                    },
                    ct);

                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJsonPlain(installResult)
                    : $"Install started: {installResult.InstallId}");
                return;
            }

            string? definitionJson = definitionInline;
            if (!string.IsNullOrWhiteSpace(definitionFile))
            {
                if (!System.IO.File.Exists(definitionFile))
                {
                    await Console.Error.WriteLineAsync($"Definition file not found: {definitionFile}");
                    Environment.Exit(1);
                    return;
                }

                // Inline takes precedence only when --definition-file is not set
                // so `--definition-file` is the canonical path; inline stays for
                // one-liners in shell scenarios.
                definitionJson = await System.IO.File.ReadAllTextAsync(definitionFile, ct);
            }

            definitionJson = ApplyCreateExecutionShorthand(
                definitionJson, inherit, image, runtime, modelProvider, model, hosting);

            var client = clientFactory();

            // CLI accepts no-dash hex Guid, dashed Guid, OR a display-name.
            // Display-name path resolves through CliResolver so the e2e
            // suite and operators can pass human names anywhere a unit id
            // is required.
            var unitIds = new List<Guid>();
            if ((units ?? Array.Empty<string>()).Any(u => !string.IsNullOrWhiteSpace(u)))
            {
                var unitResolver = new CliResolver(client);
                foreach (var raw in units!)
                {
                    if (string.IsNullOrWhiteSpace(raw))
                    {
                        continue;
                    }

                    try
                    {
                        var unitGuid = await unitResolver.ResolveUnitAsync(raw.Trim(), parentContext: null, ct);
                        unitIds.Add(unitGuid);
                    }
                    catch (CliResolutionException ex)
                    {
                        CliResolutionPrinter.Write(Console.Error, ex);
                        Environment.Exit(1);
                        return;
                    }
                }
            }

            AgentResponse result;
            try
            {
                result = await client.CreateAgentAsync(displayName, role, unitIds, definitionJson, description, ct);
            }
            catch (ApiException ex) when (ex.ResponseStatusCode == 422)
            {
                if (await UnitCommand.TryWriteMultiParentInheritanceConflictAsync(client, ex, Console.Error, ct))
                {
                    Environment.Exit(1);
                    return;
                }

                throw;
            }

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, AgentCreateColumns));
        });

        return command;
    }

    /// <summary>
    /// Parser diagnostic for <c>--connector</c> used outside the
    /// <c>--from-package</c> package-install path.
    /// </summary>
    public const string ConnectorRequiresFromPackageFlagMutexMessage =
        "error: --connector requires --from-package";

    /// <summary>
    /// Parser diagnostic for <c>--input</c> used outside the
    /// <c>--from-package</c> package-install path.
    /// </summary>
    public const string InputRequiresFromPackageFlagMutexMessage =
        "error: --input requires --from-package";

    /// <summary>
    /// Parser diagnostic for package installs mixed with inline definition input.
    /// </summary>
    public const string FromPackageDefinitionFlagMutexMessage =
        "error: --from-package is mutually exclusive with --definition and --definition-file";

    /// <summary>
    /// Parser diagnostic for package installs mixed with inherited execution mode.
    /// </summary>
    public const string FromPackageInheritFlagMutexMessage =
        "error: --from-package is mutually exclusive with --inherit";

    /// <summary>
    /// Parser diagnostic for inherited execution mixed with inline definition input.
    /// </summary>
    public const string InheritDefinitionFlagMutexMessage =
        "error: --inherit is mutually exclusive with --definition and --definition-file";

    /// <summary>
    /// Parser diagnostic for package installs mixed with execution shorthand flags.
    /// </summary>
    public const string FromPackageExecutionShorthandFlagMutexMessage =
        "error: --from-package is mutually exclusive with execution shorthands";

    /// <summary>
    /// Parser diagnostic for inherited execution mixed with execution shorthand flags.
    /// </summary>
    public const string InheritExecutionShorthandFlagMutexMessage =
        "error: --inherit is mutually exclusive with execution shorthands";

    private static IReadOnlyList<string> GetExecutionShorthandFlags(
        string? image,
        string? runtime,
        string? modelProvider,
        string? model,
        string? hosting)
    {
        var flags = new List<string>();
        if (!string.IsNullOrWhiteSpace(image)) flags.Add("--image");
        if (!string.IsNullOrWhiteSpace(runtime)) flags.Add("--runtime");
        if (!string.IsNullOrWhiteSpace(modelProvider)) flags.Add("--model-provider");
        if (!string.IsNullOrWhiteSpace(model)) flags.Add("--model");
        if (!string.IsNullOrWhiteSpace(hosting)) flags.Add("--hosting");
        return flags;
    }

    internal static string FormatFromPackageExecutionShorthandFlagMutexMessage(IReadOnlyList<string> flags)
        => $"{FromPackageExecutionShorthandFlagMutexMessage} ({FormatFlagList(flags)})";

    internal static string FormatInheritExecutionShorthandFlagMutexMessage(IReadOnlyList<string> flags)
        => $"{InheritExecutionShorthandFlagMutexMessage} ({FormatFlagList(flags)})";

    private static string FormatFlagList(IReadOnlyList<string> flags)
    {
        if (flags.Count == 0)
        {
            return string.Empty;
        }

        if (flags.Count == 1)
        {
            return flags[0];
        }

        if (flags.Count == 2)
        {
            return $"{flags[0]} and {flags[1]}";
        }

        return string.Join(", ", flags.Take(flags.Count - 1)) + ", and " + flags[^1];
    }

    private static IReadOnlyDictionary<string, string>? ParsePackageInputs(IReadOnlyList<string> inputs)
    {
        if (inputs.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in inputs)
        {
            var eqIdx = raw.IndexOf('=');
            if (eqIdx < 0)
            {
                throw new ArgumentException(
                    $"error: --input '{raw}' must be in <key>=<value> form.");
            }

            result[raw[..eqIdx]] = raw[(eqIdx + 1)..];
        }

        return result;
    }

    private static IReadOnlyDictionary<string, SpringApiClient.ConnectorBindingPayloadRequest>?
        ParsePackageConnectorBindings(IReadOnlyList<string> connectors)
    {
        if (connectors.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, SpringApiClient.ConnectorBindingPayloadRequest>(
            StringComparer.Ordinal);
        foreach (var raw in connectors)
        {
            var token = raw.Trim();
            var eqIdx = token.IndexOf('=');
            var slug = eqIdx >= 0 ? token[..eqIdx].Trim() : token;
            if (string.IsNullOrWhiteSpace(slug))
            {
                throw new ArgumentException(
                    $"error: --connector '{raw}' must be in <slug>=<binding-id> or <slug> form.");
            }

            Dictionary<string, object> config = eqIdx >= 0
                ? new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["bindingId"] = token[(eqIdx + 1)..],
                }
                : new Dictionary<string, object>(StringComparer.Ordinal);

            result[slug] = new SpringApiClient.ConnectorBindingPayloadRequest(config);
        }

        return result;
    }

    /// <summary>
    /// Merges the ADR-0038 execution-shorthand flags
    /// (<c>--image</c> / <c>--runtime</c> / <c>--model-provider</c> /
    /// <c>--model</c> / <c>--hosting</c>) into an optional
    /// agent-definition JSON string. When
    /// <paramref name="definitionJson"/> is null / empty, a fresh document
    /// carrying just the shorthand fields is produced. The structured model
    /// pair lands as <c>execution.model = {provider, id}</c>.
    /// </summary>
    /// <remarks>
    /// ADR-0039 §7 dropped the <c>--container-runtime</c> shorthand — the
    /// container runtime is platform configuration and no longer flows
    /// through this path.
    /// </remarks>
    internal static string MergeExecutionShorthand(
        string? definitionJson,
        string? image,
        string? runtime,
        string? modelProvider,
        string? model,
        string? hosting = null)
    {
        using var document = string.IsNullOrWhiteSpace(definitionJson)
            ? System.Text.Json.JsonDocument.Parse("{}")
            : System.Text.Json.JsonDocument.Parse(definitionJson);

        // Build a mutable representation.
        var properties = new Dictionary<string, System.Text.Json.JsonElement>();
        foreach (var prop in document.RootElement.EnumerateObject())
        {
            properties[prop.Name] = prop.Value.Clone();
        }

        // Preserve any existing execution block; overlay shorthand fields.
        var exec = new Dictionary<string, object?>();
        if (properties.TryGetValue("execution", out var existingExec)
            && existingExec.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var p in existingExec.EnumerateObject())
            {
                exec[p.Name] = p.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? p.Value.GetString()
                    : (object?)p.Value;
            }
        }
        if (!string.IsNullOrWhiteSpace(image)) exec["image"] = image;
        if (!string.IsNullOrWhiteSpace(runtime)) exec["runtime"] = runtime;
        if (!string.IsNullOrWhiteSpace(hosting)) exec["hosting"] = hosting;

        // ADR-0038 structured model: { provider, id }. We carry forward
        // any existing model object the caller's --definition file
        // declared, then overlay the shorthand fields on top.
        if (!string.IsNullOrWhiteSpace(modelProvider) || !string.IsNullOrWhiteSpace(model))
        {
            var modelMap = new Dictionary<string, object?>();
            if (exec.TryGetValue("model", out var existingModel)
                && existingModel is System.Text.Json.JsonElement el
                && el.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var p in el.EnumerateObject())
                {
                    modelMap[p.Name] = p.Value.ValueKind == System.Text.Json.JsonValueKind.String
                        ? p.Value.GetString()
                        : (object?)p.Value;
                }
            }
            if (!string.IsNullOrWhiteSpace(modelProvider)) modelMap["provider"] = modelProvider;
            if (!string.IsNullOrWhiteSpace(model)) modelMap["id"] = model;
            exec["model"] = modelMap;
        }

        var payload = new Dictionary<string, object?>();
        foreach (var kvp in properties)
        {
            if (!string.Equals(kvp.Key, "execution", StringComparison.OrdinalIgnoreCase))
            {
                payload[kvp.Key] = kvp.Value;
            }
        }
        payload["execution"] = exec;

        return System.Text.Json.JsonSerializer.Serialize(payload);
    }

    /// <summary>
    /// Applies the create-time execution shorthand flags unless <c>--inherit</c>
    /// requested a pure parent-inheritance create request.
    /// </summary>
    internal static string? ApplyCreateExecutionShorthand(
        string? definitionJson,
        bool inherit,
        string? image,
        string? runtime,
        string? modelProvider,
        string? model,
        string? hosting = null)
    {
        // #1901 / ADR-0039 L5: --inherit is intentionally permissive in this
        // PR. If callers also pass shorthand flags, skip the merge and let the
        // request carry the original definition JSON (or null). Mutual-
        // exclusion diagnostics land in L6.
        if (inherit)
        {
            return definitionJson;
        }

        // #409 execution-shorthand flags. Merge into the definition JSON so
        // the single server write covers everything. When the caller also
        // passed a --definition / --definition-file, the shorthand flags
        // overlay on top.
        // ADR-0038 + ADR-0039 §7: the shorthand surface is (image, runtime,
        // model-provider, model, hosting). `containerRuntime` was removed in
        // ADR-0039.
        if (!string.IsNullOrWhiteSpace(image)
            || !string.IsNullOrWhiteSpace(runtime) || !string.IsNullOrWhiteSpace(modelProvider)
            || !string.IsNullOrWhiteSpace(model) || !string.IsNullOrWhiteSpace(hosting))
        {
            return MergeExecutionShorthand(
                definitionJson, image, runtime, modelProvider, model, hosting);
        }

        return definitionJson;
    }

    // #1629 PR6 — show columns. `show` is the read-on-name verb: it
    // resolves a Guid OR a display_name to a single agent and prints the
    // canonical bits. We deliberately surface a slimmer column set than
    // `status` because `status` is the deployment-aware verb (running,
    // health, container) and `show` is the identity-aware verb.
    private static readonly OutputFormatter.Column<AgentResponse>[] AgentShowColumns =
    {
        new("id", a => GuidDisplay.Format(a.Id)),
        new("displayName", a => a.DisplayName ?? a.Name),
        new("role", a => a.Role),
        new("hosting", a => a.HostingMode),
        new("initiative", a => a.InitiativeLevel),
        new("parentUnit", a => a.ParentUnit),
    };

    private static Command CreateShowCommand(Option<string> outputOption)
    {
        // #1629 final design: every `show` accepts Guid OR display_name.
        // Guid input short-circuits to a direct lookup; name input goes
        // through CliResolver and emits a disambiguation list on n-match.
        var idArg = new Argument<string>("id-or-name")
        {
            Description =
                "The agent's stable Guid (32-char no-dash hex or dashed form), " +
                "OR a display_name to search for. When a name is supplied and " +
                "multiple agents match, a disambiguation list is printed with " +
                "each candidate's Guid.",
        };
        var unitOption = new Option<string?>("--unit")
        {
            Description =
                "Optional parent-unit context (Guid or display_name) used to " +
                "constrain a name search to that unit's members. Ignored when " +
                "the first argument is itself a Guid.",
        };
        var command = new Command(
            "show",
            "Resolve an agent by Guid OR display_name (with optional --unit context) and print its identity. " +
            "Exits non-zero with a disambiguation list when the name search is ambiguous.");
        command.Arguments.Add(idArg);
        command.Options.Add(unitOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(idArg)!;
            var unitArg = parseResult.GetValue(unitOption);
            var output = parseResult.GetValue(outputOption) ?? "table";

            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);

            // Resolve the optional parent-unit context first; if the user
            // typed a name there, the same 0/1/n disambiguation contract
            // applies. Surface a clean error message rather than letting
            // the inner agent search confuse them about which lookup
            // failed.
            Guid? unitContext = null;
            if (!string.IsNullOrWhiteSpace(unitArg))
            {
                try
                {
                    unitContext = await resolver.ResolveUnitAsync(unitArg, parentContext: null, ct);
                }
                catch (CliResolutionException ex)
                {
                    CliResolutionPrinter.Write(Console.Error, ex);
                    Environment.Exit(1);
                    return;
                }
            }

            Guid agentId;
            try
            {
                agentId = await resolver.ResolveAgentAsync(idOrName, unitContext, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            // After resolution, every server interaction speaks the
            // canonical no-dash 32-hex form (#1629 §3 wire format).
            var canonical = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(agentId);

            try
            {
                var detail = await client.GetAgentStatusAsync(canonical, ct);
                var agent = detail.Agent;

                if (agent is null)
                {
                    await Console.Error.WriteLineAsync(
                        $"Agent '{canonical}' resolved but the server returned an empty status payload.");
                    Environment.Exit(1);
                    return;
                }

                Console.WriteLine(output == "json"
                    ? OutputFormatter.FormatJson(agent)
                    : OutputFormatter.FormatTable(agent, AgentShowColumns));
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex) when (ex.ResponseStatusCode == 404)
            {
                // Direct-Guid path landed on a non-existent agent. Match
                // the resolver's 0-match wording so the operator sees the
                // same shape regardless of which mode they used.
                await Console.Error.WriteLineAsync($"No agent found matching '{idOrName}'.");
                Environment.Exit(1);
            }
        });

        return command;
    }

    private static Command CreateStatusCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("id") { Description = "The agent identifier" };
        var command = new Command("status", "Get agent status");
        command.Arguments.Add(idArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);
            string id;
            try
            {
                id = await resolver.ResolveAgentIdAsync(idOrName, unitContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            var result = await client.GetAgentStatusAsync(id, ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, AgentStatusColumns));
        });

        return command;
    }

    private static Command CreateDeleteCommand()
    {
        var idArg = new Argument<string>("id") { Description = "The agent identifier" };
        var command = new Command("delete", "Delete an agent");
        command.Arguments.Add(idArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(idArg)!;
            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);
            string id;
            try
            {
                id = await resolver.ResolveAgentIdAsync(idOrName, unitContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            await client.DeleteAgentAsync(id, ct);
            Console.WriteLine($"Agent '{idOrName}' deleted.");
        });

        return command;
    }

    private static Command CreatePurgeCommand()
    {
        var idArg = new Argument<string>("id") { Description = "The agent identifier" };
        var confirmOption = new Option<bool>("--confirm")
        {
            Description = "Required acknowledgement that this cascading delete is intentional",
        };
        var command = new Command(
            "purge",
            "Cascading cleanup: remove every membership this agent has, then delete the agent itself. Requires --confirm because it is destructive.");
        command.Arguments.Add(idArg);
        command.Options.Add(confirmOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(idArg)!;
            var confirm = parseResult.GetValue(confirmOption);
            if (!confirm)
            {
                await Console.Error.WriteLineAsync(
                    $"Refusing to purge agent '{idOrName}' without --confirm. Re-run with --confirm to proceed.");
                Environment.Exit(1);
                return;
            }

            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);
            string id;
            try
            {
                id = await resolver.ResolveAgentIdAsync(idOrName, unitContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            var renderContext = RenderContextFactory.For(
                parseResult, $"Failed to purge agent '{idOrName}'");

            try
            {
                // Step 1: enumerate memberships so users see exactly what is cascading.
                var memberships = await client.ListAgentMembershipsAsync(id, ct);
                Console.WriteLine(
                    $"Purging agent '{idOrName}': {memberships.Count} membership(s) to remove before the agent itself.");

                // Step 2: remove the agent from each unit it belongs to. Fail loud on the
                // first error so the caller can investigate before the agent is deleted.
                foreach (var membership in memberships)
                {
                    var unitId = membership.UnitId ?? string.Empty;
                    Console.WriteLine($"  - removing membership from unit '{unitId}'");
                    await client.DeleteMembershipAsync(unitId, id, ct);
                }

                // Step 3: delete the agent record.
                Console.WriteLine($"  - deleting agent '{idOrName}'");
                await client.DeleteAgentAsync(id, ct);
                Console.WriteLine($"Agent '{idOrName}' purged.");
            }
            catch (ApiException ex)
            {
                // #1068: route through the central renderer so JSON mode
                // surfaces the same operator hints that prose mode does
                // (forceHint / hint extensions on the API's purge gates).
                var exitCode = ApiExceptionRenderer.Instance.Render(ex, renderContext);
                Environment.Exit(exitCode);
            }
        });

        return command;
    }

    // Clone subcommand tree (#458) — mirrors the portal's Create/List clone
    // actions so CLI and UI stay at parity. Clone identity comes from the
    // server (a new GUID); --name is a local alias echoed back in table/JSON
    // output so callers can tag a clone when they script provisioning, but
    // it is not persisted because the server contract today has no clone
    // name field. When PR-PLAT-CLONE-1 adds persistent naming we swap the
    // local alias in for the server-side field without changing the flag.
    private static Command CreateCloneCommand(Option<string> outputOption)
    {
        var cloneCommand = new Command("clone", "Manage agent clones");
        cloneCommand.Subcommands.Add(CreateCloneCreateCommand(outputOption));
        cloneCommand.Subcommands.Add(CreateCloneListCommand(outputOption));
        // Persistent cloning-policy surface (#416). Stays under `agent clone`
        // so the mental grouping matches the enforcer's scope — every verb
        // under this tree speaks to the cloning lifecycle.
        cloneCommand.Subcommands.Add(AgentCloningPolicyCommand.Create(outputOption));
        return cloneCommand;
    }

    private static Command CreateCloneCreateCommand(Option<string> outputOption)
    {
        var agentOption = new Option<string>("--agent")
        {
            Description = "The parent agent's identifier.",
            Required = true,
        };
        var nameOption = new Option<string?>("--name")
        {
            Description = "Optional local alias for the clone, echoed in CLI output. The server assigns the canonical clone id.",
        };
        var cloneTypeOption = new Option<string>("--clone-type")
        {
            Description = "Cloning policy: none, ephemeral-no-memory (default), or ephemeral-with-memory. Matches the portal's Clone type dropdown.",
            DefaultValueFactory = _ => "ephemeral-no-memory",
        };
        cloneTypeOption.AcceptOnlyFromAmong("none", "ephemeral-no-memory", "ephemeral-with-memory");

        var attachmentOption = new Option<string>("--attachment-mode")
        {
            Description = "Attachment mode: detached (default) or attached. Matches the portal's Attachment mode dropdown.",
            DefaultValueFactory = _ => "detached",
        };
        attachmentOption.AcceptOnlyFromAmong("detached", "attached");

        var command = new Command("create", "Create a clone of an agent (same contract as the portal's Create clone action)");
        command.Options.Add(agentOption);
        command.Options.Add(nameOption);
        command.Options.Add(cloneTypeOption);
        command.Options.Add(attachmentOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var agent = parseResult.GetValue(agentOption)!;
            var alias = parseResult.GetValue(nameOption);
            var cloneTypeRaw = parseResult.GetValue(cloneTypeOption) ?? "ephemeral-no-memory";
            var attachmentRaw = parseResult.GetValue(attachmentOption) ?? "detached";
            var output = parseResult.GetValue(outputOption) ?? "table";

            var cloneType = cloneTypeRaw switch
            {
                "none" => CloningPolicy.None,
                "ephemeral-no-memory" => CloningPolicy.EphemeralNoMemory,
                "ephemeral-with-memory" => CloningPolicy.EphemeralWithMemory,
                _ => throw new InvalidOperationException($"Unexpected clone type '{cloneTypeRaw}'."),
            };
            var attachmentMode = attachmentRaw switch
            {
                "detached" => AttachmentMode.Detached,
                "attached" => AttachmentMode.Attached,
                _ => throw new InvalidOperationException($"Unexpected attachment mode '{attachmentRaw}'."),
            };

            var client = ClientFactory.Create();
            var result = await client.CreateCloneAsync(agent, cloneType, attachmentMode, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(result));
            }
            else
            {
                var row = ToCloneRow(result, alias);
                Console.WriteLine(OutputFormatter.FormatTable(row, CloneColumns));
            }
        });

        return command;
    }

    private static Command CreateCloneListCommand(Option<string> outputOption)
    {
        var agentOption = new Option<string>("--agent")
        {
            Description = "The parent agent's identifier.",
            Required = true,
        };
        var command = new Command("list", "List the clones of an agent");
        command.Options.Add(agentOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var agent = parseResult.GetValue(agentOption)!;
            var output = parseResult.GetValue(outputOption) ?? "table";

            var client = ClientFactory.Create();
            var clones = await client.ListClonesAsync(agent, ct);

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(clones));
            }
            else
            {
                var rows = new List<CloneRow>();
                foreach (var clone in clones)
                {
                    rows.Add(ToCloneRow(clone, alias: null));
                }

                Console.WriteLine(OutputFormatter.FormatTable(rows, CloneColumns));
            }
        });

        return command;
    }

    // --- Persistent-agent lifecycle commands (#396) --------------------------

    private static Command CreateDeployCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("id") { Description = "The agent identifier" };
        var imageOption = new Option<string?>("--image")
        {
            Description =
                "Optional image override for this deployment only. When omitted, " +
                "the server uses the image recorded on the agent definition.",
        };
        var replicasOption = new Option<int?>("--replicas")
        {
            Description =
                "Desired replica count. OSS core supports 0 or 1 today; horizontal scale is tracked as a follow-up.",
        };

        var command = new Command(
            "deploy",
            "Stand up (or reconcile) the backing container for a persistent agent")
        {
            idArg,
        };
        command.Options.Add(imageOption);
        command.Options.Add(replicasOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(idArg)!;
            var image = parseResult.GetValue(imageOption);
            var replicas = parseResult.GetValue(replicasOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);
            string id;
            try
            {
                id = await resolver.ResolveAgentIdAsync(idOrName, unitContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            var result = await client.DeployPersistentAgentAsync(id, image, replicas, ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, DeploymentColumns));
        });

        return command;
    }

    private static Command CreateUndeployCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("id") { Description = "The agent identifier" };
        var command = new Command(
            "undeploy",
            "Tear down the backing container for a persistent agent (distinct from `delete`, which removes the record)")
        {
            idArg,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);
            string id;
            try
            {
                id = await resolver.ResolveAgentIdAsync(idOrName, unitContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            var result = await client.UndeployPersistentAgentAsync(id, ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, DeploymentColumns));
        });

        return command;
    }

    private static Command CreateScaleCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("id") { Description = "The agent identifier" };
        var replicasOption = new Option<int>("--replicas")
        {
            Description = "Target replica count (0 = undeploy, 1 = ensure deployed; >1 is not supported yet)",
            Required = true,
        };

        var command = new Command(
            "scale",
            "Adjust replica count for a persistent agent")
        {
            idArg,
        };
        command.Options.Add(replicasOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(idArg)!;
            var replicas = parseResult.GetValue(replicasOption);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);
            string id;
            try
            {
                id = await resolver.ResolveAgentIdAsync(idOrName, unitContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            var result = await client.ScalePersistentAgentAsync(id, replicas, ct);

            Console.WriteLine(output == "json"
                ? OutputFormatter.FormatJson(result)
                : OutputFormatter.FormatTable(result, DeploymentColumns));
        });

        return command;
    }

    private static Command CreateLogsCommand()
    {
        var idArg = new Argument<string>("id") { Description = "The agent identifier" };
        var tailOption = new Option<int?>("--tail")
        {
            Description = "Number of log lines to return from the end of the container's combined output (default: 200).",
        };

        var command = new Command(
            "logs",
            "Read the last N log lines from a persistent agent's container")
        {
            idArg,
        };
        command.Options.Add(tailOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(idArg)!;
            var tail = parseResult.GetValue(tailOption);
            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);
            string id;
            try
            {
                id = await resolver.ResolveAgentIdAsync(idOrName, unitContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            var result = await client.GetPersistentAgentLogsAsync(id, tail, ct);

            // Unlike every other verb in the agent tree, logs prints the raw
            // captured text directly — operators pipe this into grep/less.
            // JSON users should use `--output` at the agent level, but the
            // default (table) surface stays plain-text.
            Console.Write(result.Logs);
            if (!string.IsNullOrEmpty(result.Logs) && !result.Logs.EndsWith('\n'))
            {
                Console.WriteLine();
            }
        });

        return command;
    }

    // #1377: `spring agent health <id>` -----------------------------------------------
    //
    // Reads health from GET /api/v1/tenant/agents/{id}/deployment which surfaces
    // healthStatus ("healthy" / "unhealthy" / "unknown"), running, and containerId.
    // This is the public API path — the lower-level dispatcher endpoint is
    // server-internal and not reachable by the CLI per CONVENTIONS.md § CLI rules.
    //
    // Exit codes: 0 = healthy, 1 = unhealthy or not deployed, 2 = unknown status.

    private sealed record HealthRow(
        string AgentId,
        string Status,
        string Running,
        string ContainerId);

    private static readonly OutputFormatter.Column<HealthRow>[] HealthColumns =
    {
        new("agentId", r => r.AgentId),
        new("status", r => r.Status),
        new("running", r => r.Running),
        new("container", r => r.ContainerId),
    };

    private static Command CreateHealthCommand(Option<string> outputOption)
    {
        var idArg = new Argument<string>("id") { Description = "The agent identifier" };
        var command = new Command(
            "health",
            "Read the health status of a persistent agent's backing container. " +
            "Exits 0 when healthy, 1 when unhealthy or not deployed, 2 when status is unknown.")
        {
            idArg,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(idArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();
            var resolver = new CliResolver(client);
            string id;
            try
            {
                id = await resolver.ResolveAgentIdAsync(idOrName, unitContext: null, ct);
            }
            catch (CliResolutionException ex)
            {
                CliResolutionPrinter.Write(Console.Error, ex);
                Environment.Exit(1);
                return;
            }

            var deployment = await client.GetPersistentAgentDeploymentAsync(id, ct);

            var status = deployment.HealthStatus ?? "unknown";
            var running = deployment.Running?.ToString().ToLowerInvariant() ?? "false";
            var container = deployment.ContainerId ?? string.Empty;

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJson(deployment));
            }
            else
            {
                var row = new HealthRow(idOrName, status, running, container);
                Console.WriteLine(OutputFormatter.FormatTable(row, HealthColumns));
            }

            // Exit code reflects the health verdict so scripts can branch on it.
            var exitCode = status switch
            {
                "healthy" => 0,
                "unknown" => 2,
                _ => 1,    // "unhealthy" or any unrecognised value
            };
            if (exitCode != 0)
            {
                Environment.Exit(exitCode);
            }
        });

        return command;
    }

    private static CloneRow ToCloneRow(CloneResponse response, string? alias) =>
        new(
            response.CloneId ?? string.Empty,
            response.ParentAgentId ?? string.Empty,
            response.CloneType?.ToString() ?? string.Empty,
            response.AttachmentMode?.ToString() ?? string.Empty,
            response.Status ?? string.Empty,
            response.CreatedAt is System.DateTimeOffset dto
                ? dto.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)
                : string.Empty,
            alias);
}
