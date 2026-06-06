Format the codebase. If $ARGUMENTS is provided, format only that target.

## Targets

| Target     | Command                                                           |
|------------|-------------------------------------------------------------------|
| dotnet     | `dotnet format SpringVoyage.slnx`                                 |
| web        | `npm run lint:fix` (eslint --fix across the portal + connectors)  |
| python     | `ruff format agents/`                                             |
| (default)  | Run all of the above                                              |

## Steps

1. Determine the target from $ARGUMENTS (or default to all)
2. Run the appropriate format command(s)
3. Report what was formatted and any remaining issues

To verify formatting / lint without changing files, run `/lint`.
