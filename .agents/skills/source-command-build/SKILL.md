---
name: "build"
description: "Run the canonical Spring Voyage build."
---

# Build

Use this skill when the user asks to run `/build`, build Spring Voyage, or verify the solution compiles.

## Command Template

Build all .NET projects.

```bash
dotnet build SpringVoyage.slnx --configuration Release
```

If the build fails, fix all errors before proceeding. Warnings are treated as errors (`TreatWarningsAsErrors` is enabled).
