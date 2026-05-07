# Operator Guide

Running a Spring Voyage deployment — installing, configuring tenants, runtimes, and connectors. Mutations go through the `spring` CLI; the portal exposes read-only views for the operator surfaces by design.

- [Deployment](deployment.md) — self-hosting on Docker Compose or Podman, including the no-build / registry path.
- [Secrets](secrets.md) — storing, rotating, and auditing tenant secrets.
- [GitHub App setup](github-app-setup.md) — per-deployment GitHub App registration.
- [BYOI agent images](byoi-agent-images.md) — bringing your own agent container images.
- [Model providers](model-providers.md) — installing and configuring per-tenant model providers (Anthropic, OpenAI, Google, Ollama). Agent runtimes are picked at unit/agent create time from the closed v0.1 catalogue and are not separately installed; see [ADR-0038](../../decisions/0038-agent-runtime-and-model-provider-split.md).
- [Connectors](connectors.md) — installing and configuring external-system connectors per tenant.
