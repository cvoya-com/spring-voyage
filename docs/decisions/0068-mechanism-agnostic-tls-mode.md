# 0068 — Mechanism-agnostic TLS: `TLS_MODE` decouples HTTPS from internet-facing

- **Status:** Accepted (2026-06-07) — `TLS_MODE` (`auto` / `internal` / `custom`) ships on **both** Caddyfiles (R1 + R3), the installer wires the scheme/issuance decision through a readiness probe that resolves [#2928](https://github.com/cvoya-com/spring-voyage/issues/2928) (R2), and the pre-existing multi-host nested-default address bug is fixed (R3). Internet-facing deployment is **not** a primary goal of the OSS platform, but HTTPS must be reachable without it — that is the gap this record closes.
- **Date:** 2026-06-07
- **Related ADRs:** [0028](0028-tenant-scoped-runtime-topology.md) — tenant-scoped runtime topology (Decision D: the `:8443` tenant-facing Caddy listener this record deliberately does *not* touch); [0042](0042-local-operator-installer.md) — local-host operator installer (the `install.sh` path R2 extends).
- **Related code:** `eng/deploy/Caddyfile` + `eng/deploy/Caddyfile.multi-host` (the `TLS_MODE` snippet import + single-level addresses); `eng/deploy/deploy.sh` (`load_env` resolves the multi-host hostname cascade; scheme-aware "stack is up" URL); `eng/install/install.sh` (R2 — the `acme_ready` probe + scheme/`TLS_MODE` decision + `spring.env` write); `eng/deploy/docker-compose.yml` (`spring-caddy` service + optional `custom` cert mount); `eng/config/spring.env.example`.
- **Related issues:** [#2928](https://github.com/cvoya-com/spring-voyage/issues/2928) — installer never enables TLS for an FQDN (the inconsistent-scheme bug R2 fixes); [#1375](https://github.com/cvoya-com/spring-voyage/issues/1375) — TLS for the *internal* tenant-facing listener (related but out of scope, see below).

## Context

Today HTTPS has exactly one path: set `DEPLOY_SCHEME=https` on a real FQDN and
Caddy obtains a Let's Encrypt certificate via the ACME HTTP-01 challenge
([`Caddyfile:25`](../../eng/deploy/Caddyfile), [`deployment.md` "TLS with
Caddy"](../guide/operator/deployment.md)). That path **structurally requires the
deployment to be internet-facing**:

- public DNS `A`/`AAAA` for the hostname must resolve to the host, and
- inbound `:80` (the HTTP-01 challenge) and `:443` must be reachable from Let's
  Encrypt's validators.

Two consequences fall out of that single path:

1. **A private deployment cannot get HTTPS at all.** Hostnames ending in
   `.localhost` / `localhost` fall back to plain HTTP by design; any *other*
   hostname triggers an ACME attempt that fails on a LAN / homelab / corp-internal
   host with no public DNS or no open `:80`. There is no self-signed, internal-CA,
   or bring-your-own-cert path for the public-facing virtual host. So the common
   OSS shape — run it on your network, *not* on the public internet — is forced
   onto plaintext HTTP, which also breaks secure-cookie and OAuth-redirect
   correctness.
2. **Rootless Podman makes it worse.** When Caddy is remapped off `80`/`443` to
   high ports (the no-sudo install path), ACME cannot validate at all, so even an
   internet-facing host loses automatic TLS ([`deployment.md` rootless
   notes](../guide/operator/deployment.md)).

[#2928](https://github.com/cvoya-com/spring-voyage/issues/2928) is the installer
face of the same problem: `install.sh` never sets `DEPLOY_SCHEME`, yet writes an
`https://…/oauth/callback` redirect URI and prints an `https://` URL — so an FQDN
install serves HTTP while advertising HTTPS, and naively flipping the scheme to
`https` would enable ACME that fails when DNS/ports aren't ready, leaving the site
unserved.

The forces: OSS operators want TLS on a deployment that is deliberately *not*
internet-facing; Caddy already ships an internal CA and a custom-cert directive
that cost nothing to expose; and the installer must never advertise a scheme it
isn't actually serving.

## Decision

**Introduce `TLS_MODE` as the certificate-issuance control, decoupling "do we
serve HTTPS, and how is the cert obtained" from "are we internet-facing."**
`DEPLOY_SCHEME` stays the `http`/`https` switch; `TLS_MODE` governs *issuance*
when the scheme is `https`. Three modes, implemented in **both** the single-host
and multi-host Caddyfiles via an env-selected snippet import
(`import spring-tls-{$TLS_MODE:auto}`; the import name is resolved by env
substitution before parse, so `internal` imports the `spring-tls-internal`
snippet):

| `TLS_MODE` | Caddy directive | Cert authority | Needs public DNS / open 80,443 |
|---|---|---|---|
| `auto` (default) | *(none — Caddy automatic HTTPS)* | Let's Encrypt for a public FQDN; Caddy internal CA for `https://localhost` | Yes, for a real cert |
| `internal` | `tls internal` | Caddy's built-in local CA | **No** |
| `custom` | `tls {$TLS_CERT_FILE} {$TLS_KEY_FILE}` | Whatever signed the supplied cert | No |

### 1. The `TLS_MODE` knob (R1)

- **`auto` preserves today's behaviour exactly.** An unset `TLS_MODE` resolves to
  `auto`, which imports an empty snippet and leaves Caddy's automatic HTTPS to
  decide (ACME for a public `https://` FQDN, internal CA for `https://localhost`,
  nothing for `http://`). Every existing install is unaffected — this change is
  purely additive.
- **`internal` is the recommended "HTTPS without internet-facing" answer.**
  Caddy's local CA issues a certificate for *any* hostname with no public DNS, no
  inbound `:80`/`:443`, and it works when Caddy is on high ports. The operator
  distributes Caddy's generated root CA (one file under the `spring-caddy-data`
  volume) to clients once; browsers trust it thereafter. This is the mode that
  closes the gap.
- **`custom` is the escape hatch for an existing PKI** — a corporate internal CA,
  a wildcard, or any externally-managed certificate. The operator supplies
  `TLS_CERT_FILE` / `TLS_KEY_FILE` and mounts the files into the `spring-caddy`
  container (a commented volume ships in `docker-compose.yml`).
- `internal` and `custom` require `DEPLOY_SCHEME=https` (a `tls` directive under an
  `http://` site is a contradiction Caddy rejects — an acceptable fail-fast on
  contradictory config; the installer sets the two together, see R2).
- **ACME stays HTTP-01 by default.** The DNS-01 challenge (a publicly-trusted cert
  for a domain you own without inbound `:80`) remains available as Caddy
  configuration for the has-a-domain-but-unreachable case; it is an ACME *tuning*,
  documented in the operator guide, not a fourth `TLS_MODE`.

### 2. Multi-host parity + the nested-default fix (R3)

`Caddyfile.multi-host` gets the same snippet import on each of its three vhosts.
That file also carried a *pre-existing* bug independent of TLS: its per-service
site addresses used a **nested** env default,
`{$WEB_HOSTNAME:{$DEPLOY_HOSTNAME:localhost}}`, and Caddy's env replacer does not
brace-count — it stops at the first `}`, corrupting every hostname with a trailing
`}`, so the file failed `caddy adapt` at `HEAD`. This record fixes it:

- **`deploy.sh` resolves the hostname cascade in bash** (where nested defaults
  work). In `load_env`, each unset `WEB/API/WEBHOOK_HOSTNAME` is resolved to
  `DEPLOY_HOSTNAME` then `localhost` and written into the env file Caddy reads, so
  every site address becomes a single-level `{$WEB_HOSTNAME:localhost}`.
- **The auto-wired `{ email {$ACME_EMAIL} }` global block is dropped** (it failed
  to parse when `ACME_EMAIL` was empty — common for `internal` / `custom`).
  Matches the single-host Caddyfile; the email is added back as a documented
  one-line global block when wanted.
- Multi-host requires three *distinct* hostnames — Caddy rejects duplicate site
  addresses, so `custom` there needs one cert covering all three (SAN / wildcard).

### 3. Installer integration (R2 — resolves [#2928](https://github.com/cvoya-com/spring-voyage/issues/2928))

`install.sh` is automation-first rather than interrogating the operator, and
never advertises a scheme it isn't serving:

- **HTTP stays the safe default**, and `REDIRECT_URI` / the printed Web URL / the
  `deploy.sh` "stack is up" line all track the *resolved* scheme (the
  [#2928](https://github.com/cvoya-com/spring-voyage/issues/2928) correctness bug).
- An **`acme_ready` probe** replaces the blind auto-https. For a non-loopback
  hostname it auto-enables `auto` (Let's Encrypt) **only** when verifiable — the
  hostname's DNS resolves to an IP on this host *and* Caddy keeps the standard
  `80`/`443`. It errs toward "not ready": a false negative keeps the safe HTTP
  default; a false positive would leave the site unserved on a failed challenge.
- When not verifiably ready, the installer **keeps HTTP and offers `internal`**
  (interactively: HTTP / private-CA HTTPS / public-ACME-anyway, default HTTP;
  non-interactive: HTTP with guidance). The chosen `TLS_MODE` is written to
  `spring.env`, and `internal` installs print how to export and trust Caddy's
  root CA.
- **Rejected: naive auto-https** (Option 3 in
  [#2928](https://github.com/cvoya-com/spring-voyage/issues/2928)) — enabling ACME
  for any non-localhost hostname can leave the site completely unserved when DNS
  isn't pointed or ports aren't open, the worst failure mode for an installer.

### Out of scope — the tenant-facing `:8443` listener ([#1375](https://github.com/cvoya-com/spring-voyage/issues/1375))

The internal tenant→platform listener (ADR-0028 Decision D) is plain HTTP on a
container-internal network. Its TLS story (internal CA / mTLS between tenant
containers and Caddy) is a *different* trust boundary — container-to-container, not
operator/browser-to-portal — and stays tracked under
[#1375](https://github.com/cvoya-com/spring-voyage/issues/1375). `TLS_MODE` governs
only the public-facing virtual host(s).

## Consequences

**Easier:**

- Any deployment can serve genuine HTTPS without being internet-facing
  (`internal`) — the structural gap is closed.
- Rootless high-port installs can have HTTPS (`internal` needs neither `:80`/`:443`
  nor ACME validation).
- Orgs plug in their existing certificate (`custom`).
- Secure cookies and OAuth redirect URIs are correct on private deployments,
  because the scheme is actually `https`.

**Harder / costs:**

- `internal` certificates are not publicly trusted: the operator must distribute
  Caddy's root CA to clients, and browsers warn until it is installed. This is the
  inherent cost of CA-less HTTPS and is documented, not hidden.
- `custom` requires mounting the cert/key into the Caddy container (a commented
  example volume ships in `docker-compose.yml`).
- There are two related knobs (`DEPLOY_SCHEME` + `TLS_MODE`); the installer (R2)
  collapses them into a single operator choice so the redundancy never surfaces in
  the common path.

**Deferred / tracked:**

- Tenant-facing internal TLS ([#1375](https://github.com/cvoya-com/spring-voyage/issues/1375)) is unchanged — a separate trust boundary (container-to-container), see *Out of scope* above.
- The `acme_ready` probe can't observe *external* reachability, so a NAT/load-balanced host (DNS → a public IP not on a local interface) reads as "not ready" and defaults to HTTP; the operator forces `DEPLOY_SCHEME=https` + `TLS_MODE=auto` in that case. A deliberately false-negative-safe trade.
- Multi-host `custom` applies one certificate to all three FQDNs (SAN / wildcard); per-host certs there are a later refinement.

**Not abstracted (deliberately):**

- No new certificate-management component — Caddy's internal CA and ACME client do
  the work; `TLS_MODE` is a thin selector over directives Caddy already ships.
- DNS-01 is exposed as ACME configuration, not promoted to a first-class mode, to
  keep the mode set to the three an operator actually chooses between.
