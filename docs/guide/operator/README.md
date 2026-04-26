# Operator Guide

Running a Spring Voyage deployment — installing, configuring tenants, runtimes, and connectors. Mutations go through the `spring` CLI; the portal exposes read-only views for the operator surfaces by design.

- [Deployment](deployment.md) — self-hosting on Docker Compose or Podman, including the no-build / registry path.
- [Secrets](secrets.md) — storing, rotating, and auditing tenant secrets.
- [GitHub App setup](github-app-setup.md) — per-deployment GitHub App registration.
- [BYOI agent images](byoi-agent-images.md) — bringing your own agent container images.
- [Agent runtimes](agent-runtimes.md) — installing and configuring agent runtimes per tenant.
- [Connectors](connectors.md) — installing and configuring external-system connectors per tenant.
