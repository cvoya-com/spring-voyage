## Description

<!-- What does this PR do? Why is it needed? -->

## Related Issues

<!--
  Repeat the closing keyword before every issue number. GitHub's parser
  reliably auto-closes only when each number has its own keyword:
    Good:       Closes #64, closes #65, closes #66.
    Good:       one per line — `- Closes #64`
    Unreliable: Closes #64, #65, #66  (silently leaves #65/#66 open)
-->

## Changes

<!-- Brief list of what changed -->

-

## Testing

- [ ] `dotnet build` passes
- [ ] `dotnet test` passes
- [ ] `dotnet format --verify-no-changes` passes
- [ ] New/changed behavior has tests

## Checklist

- [ ] Changes follow [CONVENTIONS.md](../CONVENTIONS.md)
- [ ] No breaking changes to Core interfaces (or discussed in an issue first)
- [ ] Copyright headers on new C# files
- [ ] Docs updated alongside the code per [AGENTS.md § "Documentation Updates"](../AGENTS.md#documentation-updates): relevant `docs/architecture/` and `docs/guide/` entries refreshed, and `docs/concepts/` added/updated if a new concept is introduced (or N/A for doc-only/internal-only changes)
- [ ] Doc claims match shipped behaviour; aspirational content uses a **Planned** callout ([CONVENTIONS.md § Documentation](../CONVENTIONS.md#17-documentation))
