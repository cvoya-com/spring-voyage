Run all tests.

```bash
dotnet test --solution SpringVoyage.slnx --no-restore --no-build --configuration Release
```

This matches the CI invocation in `.github/workflows/ci.yml`. Requires a prior `dotnet build --configuration Release`.

Pitfalls to avoid:
- Positional solution (`dotnet test SpringVoyage.slnx`) warns and exits 0 without running tests.
- `--nologo` triggers help output and reports "Zero tests ran" with exit 1.
- After a rebase that changes signatures, wipe `bin/`/`obj/` first — `--no-build` will happily run against stale output:
  ```bash
  find src tests -type d \( -name bin -o -name obj \) -exec rm -rf {} +
  ```

All tests must pass. If tests fail, investigate and fix before committing.
