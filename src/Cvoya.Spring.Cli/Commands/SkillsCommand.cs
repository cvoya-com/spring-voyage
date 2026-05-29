// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.CommandLine;

using Cvoya.Spring.Cli.Generated.Models;
using Cvoya.Spring.Cli.Output;

/// <summary>
/// #2361: <c>spring agent skills &lt;verb&gt;</c> and
/// <c>spring unit skills &lt;verb&gt;</c> verb trees. Operator surface
/// for equipping / unequipping / listing the skill bundles persisted
/// per-subject by <c>IUnitSkillBundleStore</c> /
/// <c>IAgentSkillBundleStore</c> (introduced under #2360). Four verbs
/// each: <c>list</c>, <c>add</c>, <c>remove</c>, <c>set</c>. All calls
/// flow through the generated <see cref="SpringApiClient"/>; the CLI
/// never builds an HTTP request by hand. Addressing is
/// <c>&lt;pkg&gt;/&lt;skill&gt;</c> — no <c>@&lt;version&gt;</c>, no
/// aliases (#2355 design Q1).
/// </summary>
public static class SkillsCommand
{
    /// <summary>
    /// Per-row shape rendered by the <c>list</c> verb. <c>Source</c>
    /// is <c>explicit</c> for everything equipped on the subject
    /// directly; inheritance from a parent unit's bundle list is
    /// deferred to #2363 (the Layer-2 member-agent inheritance hop),
    /// so today every row is <c>explicit</c>. Carrying the column on
    /// the wire shape now keeps the table format stable when #2363
    /// lands and starts emitting <c>inherited:&lt;unit_name&gt;</c>.
    /// </summary>
    private sealed record SkillRow(
        string PackageName,
        string SkillName,
        string Description,
        string Source);

    private static readonly OutputFormatter.Column<SkillRow>[] SkillColumns =
    {
        new("package_name", r => r.PackageName),
        new("skill_name", r => r.SkillName),
        new("description", r => r.Description),
        new("source", r => r.Source),
    };

    /// <summary>
    /// <c>spring agent skills</c> verb tree. Subject is the agent id /
    /// display name. Mirrors <see cref="CreateUnitSubcommand"/> for
    /// shape parity.
    /// </summary>
    public static Command CreateAgentSubcommand(Option<string> outputOption)
    {
        var command = new Command(
            "skills",
            "Equip / unequip / list the skill bundles on this agent (#2361). " +
            "Bundles are addressed by <pkg>/<skill> — no @version, no aliases.");

        command.Subcommands.Add(CreateListCommand(outputOption, agent: true));
        command.Subcommands.Add(CreateAddCommand(outputOption, agent: true));
        command.Subcommands.Add(CreateRemoveCommand(outputOption, agent: true));
        command.Subcommands.Add(CreateSetCommand(outputOption, agent: true));
        return command;
    }

    /// <summary>
    /// <c>spring unit skills</c> verb tree. Subject is the unit id /
    /// display name.
    /// </summary>
    public static Command CreateUnitSubcommand(Option<string> outputOption)
    {
        var command = new Command(
            "skills",
            "Equip / unequip / list the skill bundles on this unit (#2361). " +
            "Bundles are addressed by <pkg>/<skill> — no @version, no aliases.");

        command.Subcommands.Add(CreateListCommand(outputOption, agent: false));
        command.Subcommands.Add(CreateAddCommand(outputOption, agent: false));
        command.Subcommands.Add(CreateRemoveCommand(outputOption, agent: false));
        command.Subcommands.Add(CreateSetCommand(outputOption, agent: false));
        return command;
    }

    // ----- Verb builders ------------------------------------------------

    private static Command CreateListCommand(Option<string> outputOption, bool agent)
    {
        var subjectArg = SubjectArgument(agent);
        var command = new Command(
            "list",
            agent
                ? "List the skill bundles equipped on the agent. Bundles render in declaration order — the first row renders first in Layer 4 of the assembled prompt."
                : "List the skill bundles equipped on the unit. Bundles render in declaration order — the first row renders first in Layer 2 of the assembled prompt.");
        command.Arguments.Add(subjectArg);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(subjectArg)!;
            var output = parseResult.GetValue(outputOption) ?? "table";

            var client = ClientFactory.Create();
            var id = await ResolveSubjectAsync(client, idOrName, agent, ct);
            if (id is null) return;

            var response = await GetAsync(client, id, agent, ct);
            PrintSkillsList(response, idOrName, agent, output);
        });

        return command;
    }

    private static Command CreateAddCommand(Option<string> outputOption, bool agent)
    {
        var subjectArg = SubjectArgument(agent);
        var skillOption = new Option<string>("--skill")
        {
            Description = "Skill coordinate as <pkg>/<skill>. Idempotent on (pkg, skill).",
            Required = true,
        };

        var command = new Command(
            "add",
            agent
                ? "Equip a single skill bundle on the agent. Idempotent — re-adding refreshes the persisted prompt + required-tools snapshot without reordering."
                : "Equip a single skill bundle on the unit. Idempotent — re-adding refreshes the persisted prompt + required-tools snapshot without reordering.");
        command.Arguments.Add(subjectArg);
        command.Options.Add(skillOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(subjectArg)!;
            var skillCoord = parseResult.GetValue(skillOption)!;
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (!TryParseCoordinate(skillCoord, out var pkg, out var skill, out var error))
            {
                await Console.Error.WriteLineAsync(error);
                Environment.Exit(2);
                return;
            }

            var client = ClientFactory.Create();
            var id = await ResolveSubjectAsync(client, idOrName, agent, ct);
            if (id is null) return;

            try
            {
                var response = await PostAsync(client, id, pkg, skill, agent, ct);
                PrintSkillsList(response, idOrName, agent, output);
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await HandleApiException(ex, agent, idOrName, pkg, skill);
            }
        });

        return command;
    }

    private static Command CreateRemoveCommand(Option<string> outputOption, bool agent)
    {
        var subjectArg = SubjectArgument(agent);
        var skillOption = new Option<string>("--skill")
        {
            Description = "Skill coordinate as <pkg>/<skill>. No-op when the bundle is not equipped.",
            Required = true,
        };

        var command = new Command(
            "remove",
            agent
                ? "Unequip a single skill bundle from the agent. No-op when the bundle is not currently equipped."
                : "Unequip a single skill bundle from the unit. No-op when the bundle is not currently equipped.");
        command.Arguments.Add(subjectArg);
        command.Options.Add(skillOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(subjectArg)!;
            var skillCoord = parseResult.GetValue(skillOption)!;
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (!TryParseCoordinate(skillCoord, out var pkg, out var skill, out var error))
            {
                await Console.Error.WriteLineAsync(error);
                Environment.Exit(2);
                return;
            }

            var client = ClientFactory.Create();
            var id = await ResolveSubjectAsync(client, idOrName, agent, ct);
            if (id is null) return;

            try
            {
                var response = await DeleteAsync(client, id, pkg, skill, agent, ct);
                PrintSkillsList(response, idOrName, agent, output);
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await HandleApiException(ex, agent, idOrName, pkg, skill);
            }
        });

        return command;
    }

    private static Command CreateSetCommand(Option<string> outputOption, bool agent)
    {
        var subjectArg = SubjectArgument(agent);
        var skillsOption = new Option<string>("--skills")
        {
            Description =
                "Comma-separated bundle list as <pkg1>/<skill1>,<pkg2>/<skill2>. " +
                "Pass an empty string to clear. Replaces the persisted list — bundles not " +
                "in the new list are unequipped, new ones are equipped, ordering follows the flag.",
            Required = true,
        };

        var command = new Command(
            "set",
            agent
                ? "Bulk-replace the agent's equipped skill bundles. The API has no atomic bulk write — the CLI composes the diff (remove dropped, add new); a mid-flight failure leaves the subject in a partially-applied state."
                : "Bulk-replace the unit's equipped skill bundles. The API has no atomic bulk write — the CLI composes the diff (remove dropped, add new); a mid-flight failure leaves the subject in a partially-applied state.");
        command.Arguments.Add(subjectArg);
        command.Options.Add(skillsOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken ct) =>
        {
            var idOrName = parseResult.GetValue(subjectArg)!;
            var skillsRaw = parseResult.GetValue(skillsOption) ?? string.Empty;
            var output = parseResult.GetValue(outputOption) ?? "table";

            if (!TryParseCoordinateList(skillsRaw, out var targets, out var error))
            {
                await Console.Error.WriteLineAsync(error);
                Environment.Exit(2);
                return;
            }

            var client = ClientFactory.Create();
            var id = await ResolveSubjectAsync(client, idOrName, agent, ct);
            if (id is null) return;

            try
            {
                var response = await ApplySetAsync(client, id, targets, agent, ct);
                PrintSkillsList(response, idOrName, agent, output);
            }
            catch (Microsoft.Kiota.Abstractions.ApiException ex)
            {
                await HandleApiException(ex, agent, idOrName, packageName: null, skillName: null);
            }
        });

        return command;
    }

    // ----- Bulk-set diff/apply ------------------------------------------

    /// <summary>
    /// The ordered equip/unequip plan produced by <see cref="ComputeSetPlan"/>:
    /// the bundles to unequip (in the server's current order) followed by the
    /// bundles to equip (in operator order). Extracted (#2902) so the diff is
    /// a pure, directly-testable unit — the prior <c>ApplySetAsync</c> only
    /// had parse-only + wrapper coverage, so a regression that dropped the
    /// remove step or reordered the adds would have shipped green.
    /// </summary>
    internal sealed record SkillSetPlan(
        IReadOnlyList<(string Package, string Skill)> Removes,
        IReadOnlyList<(string Package, string Skill)> Adds);

    /// <summary>
    /// Pure diff: given the subject's <paramref name="current"/> equipped
    /// list and the operator's <paramref name="targets"/>, returns the
    /// bundles to unequip (everything currently equipped that is not a
    /// target, in the server's current order) and the bundles to (re-)equip
    /// (every target, in operator order). Targets are re-asserted even when
    /// already equipped — POST is idempotent server-side and re-posting
    /// refreshes the persisted prompt / required-tools snapshot, matching the
    /// store's add-then-refresh semantics. The order of <see cref="SkillSetPlan.Adds"/>
    /// is the persisted declaration order on the wire.
    /// </summary>
    internal static SkillSetPlan ComputeSetPlan(
        IReadOnlyList<EquippedSkillEntry> current,
        IReadOnlyList<(string Package, string Skill)> targets)
    {
        var targetKeys = new HashSet<string>(
            targets.Select(t => Key(t.Package, t.Skill)),
            StringComparer.Ordinal);

        var removes = current
            .Where(e => !targetKeys.Contains(Key(e.PackageName, e.SkillName)))
            .Select(e => (e.PackageName ?? string.Empty, e.SkillName ?? string.Empty))
            .ToList();

        // Re-assert every target (kept or new) in operator order.
        var adds = targets.ToList();

        return new SkillSetPlan(removes, adds);
    }

    /// <summary>
    /// Composes the equip/unequip diff against the server's current list and
    /// applies it. The unequip step is run first so a subsequent equip can't
    /// fail with a 400 just because the new list reorders an already-equipped
    /// bundle that was about to be removed. The order of the <c>POST</c> calls
    /// determines the persisted declaration order — the API store appends, and
    /// the operator-supplied flag order is the declaration order on the wire.
    /// The diff itself lives in <see cref="ComputeSetPlan"/>.
    /// </summary>
    internal static async Task<EquippedSkillsResponse> ApplySetAsync(
        SpringApiClient client,
        string id,
        IReadOnlyList<(string Package, string Skill)> targets,
        bool agent,
        CancellationToken ct)
    {
        var current = await GetAsync(client, id, agent, ct);
        var plan = ComputeSetPlan(current.Skills ?? new List<EquippedSkillEntry>(), targets);

        EquippedSkillsResponse latest = current;

        // Remove dropped bundles first.
        foreach (var (pkg, skill) in plan.Removes)
        {
            latest = await DeleteAsync(client, id, pkg, skill, agent, ct);
        }

        // Equip the new (or re-asserted) bundles in operator order.
        foreach (var (pkg, skill) in plan.Adds)
        {
            latest = await PostAsync(client, id, pkg, skill, agent, ct);
        }

        return latest;
    }

    private static string Key(string? pkg, string? skill)
        => $"{pkg ?? string.Empty}/{skill ?? string.Empty}";

    // ----- Argument / option factories ----------------------------------

    private static Argument<string> SubjectArgument(bool agent)
        => agent
            ? new("agent") { Description = "Agent id or display_name." }
            : new("unit") { Description = "Unit id or display_name." };

    // ----- Coordinate parsing -------------------------------------------

    /// <summary>
    /// Splits <c>&lt;pkg&gt;/&lt;skill&gt;</c> on the first <c>/</c>. The
    /// package name may itself contain slashes (e.g.
    /// <c>spring-voyage/software-engineering</c>) — the skill is always
    /// the final segment, so we split from the right.
    /// </summary>
    internal static bool TryParseCoordinate(
        string raw,
        out string packageName,
        out string skillName,
        out string error)
    {
        packageName = string.Empty;
        skillName = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "--skill is required and must have the form <pkg>/<skill>.";
            return false;
        }

        var trimmed = raw.Trim();
        var lastSlash = trimmed.LastIndexOf('/');
        if (lastSlash <= 0 || lastSlash == trimmed.Length - 1)
        {
            error = $"--skill '{raw}' must have the form <pkg>/<skill> (no @version, no aliases).";
            return false;
        }

        packageName = trimmed[..lastSlash];
        skillName = trimmed[(lastSlash + 1)..];

        if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(skillName))
        {
            error = $"--skill '{raw}' must have the form <pkg>/<skill>.";
            return false;
        }

        return true;
    }

    internal static bool TryParseCoordinateList(
        string raw,
        out IReadOnlyList<(string Package, string Skill)> targets,
        out string error)
    {
        error = string.Empty;
        var list = new List<(string Package, string Skill)>();
        targets = list;

        if (string.IsNullOrWhiteSpace(raw))
        {
            // Empty string is the canonical "clear" form.
            return true;
        }

        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseCoordinate(token, out var pkg, out var skill, out var innerError))
            {
                error = innerError;
                return false;
            }

            // Reject duplicates in the input list — the user almost
            // certainly didn't mean to equip the same bundle twice, and
            // letting it through silently would mask a typo upstream.
            if (list.Any(t => t.Package == pkg && t.Skill == skill))
            {
                error = $"--skills contains duplicate coordinate '{pkg}/{skill}'.";
                return false;
            }

            list.Add((pkg, skill));
        }

        return true;
    }

    // ----- Subject resolution -------------------------------------------

    private static async Task<string?> ResolveSubjectAsync(
        SpringApiClient client, string idOrName, bool agent, CancellationToken ct)
    {
        var resolver = new CliResolver(client);
        try
        {
            return agent
                ? await resolver.ResolveAgentIdAsync(idOrName, unitContext: null, ct)
                : await resolver.ResolveUnitIdAsync(idOrName, parentContext: null, ct);
        }
        catch (CliResolutionException ex)
        {
            CliResolutionPrinter.Write(Console.Error, ex);
            Environment.Exit(1);
            return null;
        }
    }

    // ----- API wrappers -------------------------------------------------

    private static Task<EquippedSkillsResponse> GetAsync(
        SpringApiClient client, string id, bool agent, CancellationToken ct)
        => agent
            ? client.GetAgentSkillsAsync(id, ct)
            : client.GetUnitSkillsAsync(id, ct);

    private static Task<EquippedSkillsResponse> PostAsync(
        SpringApiClient client, string id, string packageName, string skillName, bool agent, CancellationToken ct)
        => agent
            ? client.EquipAgentSkillAsync(id, packageName, skillName, ct)
            : client.EquipUnitSkillAsync(id, packageName, skillName, ct);

    private static Task<EquippedSkillsResponse> DeleteAsync(
        SpringApiClient client, string id, string packageName, string skillName, bool agent, CancellationToken ct)
        => agent
            ? client.UnequipAgentSkillAsync(id, packageName, skillName, ct)
            : client.UnequipUnitSkillAsync(id, packageName, skillName, ct);

    // ----- Output -------------------------------------------------------

    private static void PrintSkillsList(EquippedSkillsResponse response, string idOrName, bool agent, string output)
    {
        if (output == "json")
        {
            Console.WriteLine(OutputFormatter.FormatJson(response));
            return;
        }

        var entries = response.Skills ?? new List<EquippedSkillEntry>();
        if (entries.Count == 0)
        {
            var noun = agent ? "agent" : "unit";
            Console.WriteLine($"No skill bundles equipped on {noun} '{idOrName}'.");
            return;
        }

        var rows = entries.Select(ToRow).ToList();
        Console.WriteLine(OutputFormatter.FormatTable(rows, SkillColumns));
    }

    private static SkillRow ToRow(EquippedSkillEntry entry) => new(
        PackageName: entry.PackageName ?? string.Empty,
        SkillName: entry.SkillName ?? string.Empty,
        // Until #2363 lands the inheritance hop, the API only returns
        // what's directly equipped on the subject — so the description
        // is the bundle's own prompt summary and the source is always
        // 'explicit'. Both columns are stable across that future change.
        Description: entry.PromptSummary ?? string.Empty,
        Source: "explicit");

    // ----- Error handling -----------------------------------------------

    private static async Task HandleApiException(
        Microsoft.Kiota.Abstractions.ApiException ex,
        bool agent,
        string idOrName,
        string? packageName,
        string? skillName)
    {
        var subject = agent ? "agent" : "unit";
        var subjectPath = agent ? "agents" : "units";

        switch (ex.ResponseStatusCode)
        {
            case 404:
                // The API returns 404 for both subject-not-found (the {id} path segment)
                // and skill-coordinate-not-found on DELETE (when the legacy 404 path
                // fires). Disambiguate using the skill arg presence.
                if (packageName is not null && skillName is not null)
                {
                    await Console.Error.WriteLineAsync(
                        $"Not found: either {subject} '{idOrName}' does not exist, or '{packageName}/{skillName}' is not equipped on it. " +
                        $"Run 'spring {subjectPath.TrimEnd('s')} skills list {idOrName}' to inspect the current list.");
                }
                else
                {
                    await Console.Error.WriteLineAsync(
                        $"{Capitalize(subject)} '{idOrName}' not found.");
                }
                Environment.Exit(1);
                return;

            case 400:
                // The equip endpoint returns 400 when the skill coordinate
                // references a package not installed on the tenant or a
                // skill not in the installed package (the two
                // SkillBundlePackageNotFound / SkillBundleNotFound paths
                // in the backend). Surface the server's detail message
                // so operators get a precise next step.
                await Console.Error.WriteLineAsync(BuildErrorMessage(
                    $"Cannot equip skill on {subject} '{idOrName}'", ex));
                Environment.Exit(1);
                return;

            case 409:
                await Console.Error.WriteLineAsync(BuildErrorMessage(
                    $"Conflict updating skills on {subject} '{idOrName}'", ex));
                Environment.Exit(1);
                return;

            default:
                await Console.Error.WriteLineAsync(BuildErrorMessage(
                    $"API error updating skills on {subject} '{idOrName}'", ex));
                Environment.Exit(1);
                return;
        }
    }

    private static string BuildErrorMessage(string prefix, Microsoft.Kiota.Abstractions.ApiException ex)
    {
        // Prefer the ProblemDetails 'detail' when the server emitted
        // one. Kiota's ApiException carries .Message; the typed
        // ProblemDetails path is per-endpoint and not all DELETE/POST
        // surface it here, so .Message is the reliable fallback.
        if (ex is ProblemDetails pd && !string.IsNullOrWhiteSpace(pd.Detail))
        {
            return $"{prefix}: {pd.Detail}";
        }
        return string.IsNullOrWhiteSpace(ex.Message)
            ? $"{prefix}: server returned status {ex.ResponseStatusCode}."
            : $"{prefix}: {ex.Message}";
    }

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
