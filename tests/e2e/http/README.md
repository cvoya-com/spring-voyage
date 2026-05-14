# `tests/e2e/http/` — Web API end-to-end tests (reserved)

This directory is reserved for **out-of-process** Web API end-to-end smoke
tests that hit the deployed `spring-api` host over HTTP.

It is intentionally empty today. In-process API tests live in
`tests/integration/Cvoya.Spring.Integration.Tests/` (`WebApplicationFactory`-
based), which is the right home for anything that exercises endpoint
behaviour without spinning up containers. Add a project here only when an
end-to-end scenario genuinely needs the real network surface (TLS, Caddy,
Dapr sidecar, container DNS, etc.).
