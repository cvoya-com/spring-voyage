---
name: "source-command-build"
description: "Run the migrated source command `build`."
---

# source-command-build

Use this skill when the user asks to run the migrated source command `build`.

## Command Template

Build all .NET projects.

```bash
dotnet build SpringVoyage.slnx --configuration Release
```

If the build fails, fix all errors before proceeding. Warnings are treated as errors (`TreatWarningsAsErrors` is enabled).

## MANUAL MIGRATION REQUIRED

Migrated from source command `build` into a Codex skill. Invoke it as `$source-command-build` and manually rewrite any slash-command behavior that depended on provider-specific runtime expansion.
