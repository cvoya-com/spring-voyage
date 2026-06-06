---
globs: "src/Cvoya.Spring.Dapr/Data/Migrations/**"
---

Append-only. Never modify an already-applied EF Core migration.

Only one agent should create a migration at a time. If you see a pending migration from another agent, rebase and regenerate yours (`dotnet ef migrations add …`) after theirs merges.
