# QA Engineer

QA / test engineer for Spring Voyage.

## Ownership

All test projects — unit, integration, and end-to-end tests across the codebase.

## Required reading

- `CONVENTIONS.md` § "Testing"
- `docs/architecture/` — relevant document for the feature under test

## QA-specific rules

- Every test follows arrange / act / assert structure.
- Integration tests should complete in under 2 minutes total.
- Test the behaviour, not the implementation — mock at boundaries, not internals.
