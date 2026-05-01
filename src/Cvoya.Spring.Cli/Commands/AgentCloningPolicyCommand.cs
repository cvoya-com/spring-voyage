// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;
using System.IO;
using System.Text;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;


using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Builds the <c>spring agent clone policy get|set|clear</c> subtree (#416).
/// Operator-facing surface for the persistent cloning-policy record the
/// enforcer consults on every clone request. Mirrors the portal's
/// cloning-policy panel (tracked as a follow-up portal PR) so CLI and UI
/// stay at parity.
/// </summary>
/// <remarks>
/// <para>
/// Scope flag. <c>--scope agent</c> (default) targets the agent-scoped
/// policy and requires the <c>&lt;id&gt;</c> argument. <c>--scope tenant</c>
/// targets the tenant-wide policy — the <c>&lt;id&gt;</c> argument is
/// ignored because the server resolves the tenant from <c>ITenantContext</c>.
/// This keeps a single verb covering both surfaces rather than a second
/// top-level verb.
/// </para>
/// <para>
/// <c>set</c> replaces the policy in full (the server treats an empty-body
/// PUT as "clear all constraints"). Per-flag editing is intentionally
/// avoided because the policy is a small record — callers either supply
/// every intended slot via flags, or use <c>-f &lt;file&gt;</c> with a YAML
/// fragment that captures the whole shape.
/// </para>
/// </remarks>
public static class AgentCloningPolicyCommand
{
    /// <summary>
    /// Returns the <c>policy</c> verb tree for attachment under
    /// <c>spring agent clone</c>.
    /// </summary>
    public static Command Create(Option<string> outputOption)
    {
        var command = new Command("policy",
            "Manage the persistent cloning policy for an agent (or the tenant-wide default).");
        command.Subcommands.Add(CreateGet(outputOption));
        command.Subcommands.Add(CreateSet(outputOption));
        command.Subcommands.Add(CreateClear());
        return command;
    }

    private static Option<string> ScopeOption() => new Option<string>("--scope")
    {
        Description = "Policy scope: 'agent' (default; requires <id>) or 'tenant' (ignores <id>).",
        DefaultValueFactory = _ => "agent",
    }.AcceptOnlyScopes();

    private static Option<string> AcceptOnlyScopes(this Option<string> option)
    {
        option.AcceptOnlyFromAmong("agent", "tenant");
        return option;
    }

    // ---- get ---------------------------------------------------------------

    private static Command CreateGet(Option<string> outputOption)
    {
        var idArg = new Argument<string?>("id")
        {
            Description = "Agent identifier (required for --scope agent; ignored for --scope tenant).",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var scopeOption = ScopeOption();
        var command = new Command("get", "Print the persistent cloning policy for the selected scope.");
        command.Arguments.Add(idArg);
        command.Options.Add(scopeOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var scope = parseResult.GetValue(scopeOption) ?? "agent";
            var id = parseResult.GetValue(idArg);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var client = ClientFactory.Create();

            AgentCloningPolicyResponse policy;
            string label;
            if (scope == "tenant")
            {
                policy = await client.GetTenantCloningPolicyAsync(ct);
                label = "tenant";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    await Console.Error.WriteLineAsync(
                        "Agent id is required for --scope agent.");
                    Environment.Exit(1);
                    return;
                }
                policy = await client.GetAgentCloningPolicyAsync(id!, ct);
                label = $"agent '{id}'";
            }

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(ToPlainProjection(policy)));
                return;
            }
            Console.Write(FormatForHumans(label, policy));
        });

        return command;
    }

    // ---- set ---------------------------------------------------------------

    private static Command CreateSet(Option<string> outputOption)
    {
        var idArg = new Argument<string?>("id")
        {
            Description = "Agent identifier (required for --scope agent; ignored for --scope tenant).",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var scopeOption = ScopeOption();
        var fileOption = new Option<string?>("--file", "-f")
        {
            Description =
                "YAML fragment describing the full cloning policy (allowed-policies, allowed-attachment-modes, " +
                "max-clones, max-depth, budget). Replaces the stored policy in full.",
        };
        var allowedPoliciesOption = new Option<string[]?>("--allowed-policy")
        {
            Description =
                "Add an entry to the allowed-policies list. One of 'none', 'ephemeral-no-memory', " +
                "'ephemeral-with-memory'. Repeat for multiple values. Pass nothing to leave the list " +
                "unconstrained.",
            AllowMultipleArgumentsPerToken = false,
        };
        var allowedAttachmentsOption = new Option<string[]?>("--allowed-attachment")
        {
            Description =
                "Add an entry to the allowed-attachment-modes list. One of 'detached', 'attached'. Repeat " +
                "for multiple values. Pass nothing to leave the list unconstrained.",
            AllowMultipleArgumentsPerToken = false,
        };
        var maxClonesOption = new Option<int?>("--max-clones")
        {
            Description = "Maximum concurrent clones permitted at this scope.",
        };
        var maxDepthOption = new Option<int?>("--max-depth")
        {
            Description =
                "Maximum recursive cloning depth. 0 disables recursive cloning; omit to defer to the " +
                "platform default.",
        };
        var budgetOption = new Option<decimal?>("--budget")
        {
            Description = "Per-clone cost budget forwarded to the validation activity.",
        };

        var command = new Command("set", "Replace the persistent cloning policy in full.");
        command.Arguments.Add(idArg);
        command.Options.Add(scopeOption);
        command.Options.Add(fileOption);
        command.Options.Add(allowedPoliciesOption);
        command.Options.Add(allowedAttachmentsOption);
        command.Options.Add(maxClonesOption);
        command.Options.Add(maxDepthOption);
        command.Options.Add(budgetOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var scope = parseResult.GetValue(scopeOption) ?? "agent";
            var id = parseResult.GetValue(idArg);
            var output = parseResult.GetValue(outputOption) ?? "table";
            var file = parseResult.GetValue(fileOption);
            var client = ClientFactory.Create();

            AgentCloningPolicyResponse body;
            if (!string.IsNullOrWhiteSpace(file))
            {
                if (!File.Exists(file))
                {
                    await Console.Error.WriteLineAsync($"File not found: {file}");
                    Environment.Exit(1);
                    return;
                }
                body = ParsePolicyFromYaml(await File.ReadAllTextAsync(file, ct));
            }
            else
            {
                body = BuildFromFlags(
                    parseResult.GetValue(allowedPoliciesOption),
                    parseResult.GetValue(allowedAttachmentsOption),
                    parseResult.GetValue(maxClonesOption),
                    parseResult.GetValue(maxDepthOption),
                    parseResult.GetValue(budgetOption));
            }

            AgentCloningPolicyResponse stored;
            string label;
            if (scope == "tenant")
            {
                stored = await client.SetTenantCloningPolicyAsync(body, ct);
                label = "tenant";
            }
            else
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    await Console.Error.WriteLineAsync(
                        "Agent id is required for --scope agent.");
                    Environment.Exit(1);
                    return;
                }
                stored = await client.SetAgentCloningPolicyAsync(id!, body, ct);
                label = $"agent '{id}'";
            }

            if (output == "json")
            {
                Console.WriteLine(OutputFormatter.FormatJsonPlain(ToPlainProjection(stored)));
            }
            else
            {
                Console.WriteLine($"{ToTitleCase(label)} cloning policy updated.");
                Console.Write(FormatForHumans(label, stored));
            }
        });

        return command;
    }

    // ---- clear -------------------------------------------------------------

    private static Command CreateClear()
    {
        var idArg = new Argument<string?>("id")
        {
            Description = "Agent identifier (required for --scope agent; ignored for --scope tenant).",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var scopeOption = ScopeOption();
        var command = new Command("clear", "Remove every constraint from the persistent cloning policy at this scope.");
        command.Arguments.Add(idArg);
        command.Options.Add(scopeOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var scope = parseResult.GetValue(scopeOption) ?? "agent";
            var id = parseResult.GetValue(idArg);
            var client = ClientFactory.Create();

            if (scope == "tenant")
            {
                await client.ClearTenantCloningPolicyAsync(ct);
                Console.WriteLine("Tenant cloning policy cleared.");
                return;
            }

            if (string.IsNullOrWhiteSpace(id))
            {
                await Console.Error.WriteLineAsync("Agent id is required for --scope agent.");
                Environment.Exit(1);
                return;
            }

            await client.ClearAgentCloningPolicyAsync(id!, ct);
            Console.WriteLine($"Agent '{id}' cloning policy cleared.");
        });

        return command;
    }

    // ---- helpers -----------------------------------------------------------

    private static AgentCloningPolicyResponse BuildFromFlags(
        string[]? allowedPolicies,
        string[]? allowedAttachments,
        int? maxClones,
        int? maxDepth,
        decimal? budget)
    {
        var policy = new AgentCloningPolicyResponse();
        if (allowedPolicies is { Length: > 0 })
        {
            policy.AllowedPolicies = allowedPolicies
                .Select(ParseCloningPolicyValue)
                .Cast<CloningPolicy?>()
                .ToList();
        }
        if (allowedAttachments is { Length: > 0 })
        {
            policy.AllowedAttachmentModes = allowedAttachments
                .Select(ParseAttachmentModeValue)
                .Cast<AttachmentMode?>()
                .ToList();
        }
        if (maxClones.HasValue)
        {
            policy.MaxClones = maxClones.Value;
        }
        if (maxDepth.HasValue)
        {
            policy.MaxDepth = maxDepth.Value;
        }
        if (budget.HasValue)
        {
            policy.Budget = (double)budget.Value;
        }
        return policy;
    }

    private static CloningPolicy ParseCloningPolicyValue(string raw) => raw switch
    {
        "none" => CloningPolicy.None,
        "ephemeral-no-memory" => CloningPolicy.EphemeralNoMemory,
        "ephemeral-with-memory" => CloningPolicy.EphemeralWithMemory,
        _ => throw new InvalidOperationException(
            $"Unknown cloning policy '{raw}'. Expected one of: none, ephemeral-no-memory, ephemeral-with-memory."),
    };

    private static AttachmentMode ParseAttachmentModeValue(string raw) => raw switch
    {
        "detached" => AttachmentMode.Detached,
        "attached" => AttachmentMode.Attached,
        _ => throw new InvalidOperationException(
            $"Unknown attachment mode '{raw}'. Expected one of: detached, attached."),
    };

    private static AgentCloningPolicyResponse ParsePolicyFromYaml(string yamlText)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(HyphenatedNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var parsed = deserializer.Deserialize<YamlPolicy>(yamlText) ?? new YamlPolicy();

        return BuildFromFlags(
            parsed.AllowedPolicies?.ToArray(),
            parsed.AllowedAttachmentModes?.ToArray(),
            parsed.MaxClones,
            parsed.MaxDepth,
            parsed.Budget);
    }

    private static object ToPlainProjection(AgentCloningPolicyResponse policy) => new
    {
        allowedPolicies = policy.AllowedPolicies?.Where(p => p.HasValue).Select(p => CloningPolicyToWire(p!.Value)).ToList(),
        allowedAttachmentModes = policy.AllowedAttachmentModes?.Where(m => m.HasValue).Select(m => AttachmentModeToWire(m!.Value)).ToList(),
        maxClones = policy.MaxClones,
        maxDepth = policy.MaxDepth,
        budget = policy.Budget,
    };

    private static string CloningPolicyToWire(CloningPolicy policy) => policy switch
    {
        CloningPolicy.None => "none",
        CloningPolicy.EphemeralNoMemory => "ephemeral-no-memory",
        CloningPolicy.EphemeralWithMemory => "ephemeral-with-memory",
        _ => policy.ToString(),
    };

    private static string AttachmentModeToWire(AttachmentMode mode) => mode switch
    {
        AttachmentMode.Detached => "detached",
        AttachmentMode.Attached => "attached",
        _ => mode.ToString(),
    };

    private static string FormatForHumans(string label, AgentCloningPolicyResponse policy)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Scope: {label}");
        sb.AppendLine();

        sb.AppendLine("Allowed cloning policies:");
        if (policy.AllowedPolicies is null || policy.AllowedPolicies.Count == 0)
        {
            sb.AppendLine("  (unconstrained)");
        }
        else
        {
            foreach (var value in policy.AllowedPolicies)
            {
                if (value.HasValue)
                {
                    sb.AppendLine($"  - {CloningPolicyToWire(value.Value)}");
                }
            }
        }

        sb.AppendLine("Allowed attachment modes:");
        if (policy.AllowedAttachmentModes is null || policy.AllowedAttachmentModes.Count == 0)
        {
            sb.AppendLine("  (unconstrained)");
        }
        else
        {
            foreach (var value in policy.AllowedAttachmentModes)
            {
                if (value.HasValue)
                {
                    sb.AppendLine($"  - {AttachmentModeToWire(value.Value)}");
                }
            }
        }

        sb.AppendLine($"Max clones:     {policy.MaxClones?.ToString() ?? "(unconstrained)"}");
        sb.AppendLine($"Max depth:      {policy.MaxDepth?.ToString() ?? "(unconstrained)"}");
        sb.AppendLine($"Budget:         {(policy.Budget.HasValue ? policy.Budget.Value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : "(unconstrained)")}");

        return sb.ToString();
    }

    private static string ToTitleCase(string value) =>
        value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..];

    /// <summary>
    /// YAML-shaped mirror of the policy. Kept parallel to the wire shape so
    /// YamlDotNet doesn't need to grok Kiota's composed types.
    /// </summary>
    private sealed class YamlPolicy
    {
        public List<string>? AllowedPolicies { get; set; }
        public List<string>? AllowedAttachmentModes { get; set; }
        public int? MaxClones { get; set; }
        public int? MaxDepth { get; set; }
        public decimal? Budget { get; set; }
    }
}