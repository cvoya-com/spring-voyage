# 0069 — Podman version floor is 5.4 (project-wide); the rootless-bridge `host.containers.internal` bug is fixed by-floor

- **Status:** Accepted (2026-06-07) — the supported Podman floor rises from 4.4 to **5.4** across the operator install path, the build-host preflight, and all prerequisite docs. This supersedes the broad "Podman 4+/4.4+" stance carried by [0042](0042-local-operator-installer.md) and resolves [#2927](https://github.com/cvoya-com/spring-voyage/issues/2927) by-floor, dropping the deferred `--add-host=host.containers.internal:host-gateway` workaround that issue first proposed.
- **Date:** 2026-06-07
- **Related ADRs:** [0042](0042-local-operator-installer.md) — local-host operator installer (the `install.sh` Podman gate this record raises; its "Podman 4+" prose is historical and not re-edited); [0012](0012-spring-dispatcher-service-extraction.md) / [0058](0058-spring-voyage-container-contract.md) — the host-process dispatcher and container contract that depend on a container reaching the host over `host.containers.internal`.
- **Related code:** `eng/install/install.sh` (the load-bearing gate — major-only → major.minor compare); `eng/build/build.sh` + `eng/deploy/setup.sh` (build-host preflights — literal floor bump, Docker path unchanged at 23.0+); `eng/install/tests/test-install.sh` (fixture podman stub bumped to clear the floor); the `host.containers.internal` reachability described in `docs/guide/operator/deployment.md`.
- **Related issues:** [#2927](https://github.com/cvoya-com/spring-voyage/issues/2927) — rootless `host.containers.internal` on the `spring-net` bridge (resolved here by-floor).

## Context

Every Spring Voyage container attaches to the user-defined bridge network
`spring-net`, and the worker / API reach the host-process dispatcher
([0012](0012-spring-dispatcher-service-extraction.md)) over the
`host.containers.internal` DNS name. On rootless Podman that name only resolves
to the host once two things are true:

- **pasta is the rootless network backend** — Podman switched the rootless
  default from `slirp4netns` to **pasta** at **5.0.0**; pasta's `--map-guest-addr`
  is what makes `host.containers.internal` resolve to the host by default.
- **the rootless-bridge entry bug is fixed** — Podman **5.3.0** fixed a bug where
  *"an incorrect `host.containers.internal` entry could be added when running
  rootless containers using the bridge network mode"* (Podman `RELEASE_NOTES`).
  Because we run on the user-defined `spring-net` bridge, this is exactly the path
  the fix addresses.

The prior floor was Podman **4.4** — chosen as the buildah-1.28 / `COPY --parents`
minimum the platform Dockerfile needs, **not** for the networking. At 4.4–4.9 the
rootless default is still `slirp4netns` and no bridge-mode fix exists, so
[#2927](https://github.com/cvoya-com/spring-voyage/issues/2927) reported the
worker failing to reach the dispatcher on a rootless deploy.

[#2927](https://github.com/cvoya-com/spring-voyage/issues/2927) first proposed
keeping the 4.4 floor and adding `--add-host=host.containers.internal:host-gateway`
to the worker and API container launches. That workaround was deferred (a "core
networking change needing validation across the version window"). The forces that
reframe it: the only Podman version that *actually* deletes the workaround is
**5.3+**; and @savasp set a concrete supported-distro target whose lowest member
already ships well past that boundary — so the floor itself can carry the fix and
the per-container flag becomes unnecessary.

## Decision

**Raise the supported Podman floor to 5.4, project-wide, and resolve the rootless
`host.containers.internal` bug by-floor rather than with `--add-host`.**

**5.4 is the floor.** It is the highest floor that still admits every supported
distro, and it sits comfortably past the 5.3 boundary where the rootless-bridge
fix lands:

| Distro (target) | Podman shipped | ≥ 5.4? |
|---|---|---|
| Ubuntu 26.04 LTS | ~5.7 | yes |
| Debian 13.5 (trixie) | **5.4.2** (frozen) | yes — the binding lowest |
| RHEL 10.2 | ~5.5–5.7 | yes |
| Fedora 44 | 5.8+ | yes |

Debian 13.5's **5.4.2** is the lowest-common-denominator. A higher floor (e.g.
5.8) would reject stock Debian 13.5 and likely RHEL 10.2, forcing backports on two
of the four targets for no additional benefit; 5.4 buys the identical fix without
dropping any distro.

**Compare major.minor, not a string or major-only.** A floor of 5.4 must reject
5.0–5.3 (pasta present but the bridge bug unfixed) while accepting 5.4+. The
acceptance predicate is `major > 5 || (major == 5 && minor >= 4)`, with a
non-numeric parse treated as a failure. Three call sites enforce it:

- **`eng/install/install.sh`** (load-bearing) — was a **major-only** compare
  (`${version%%.*}` then `-lt 4`), which a 5.4 floor cannot express; rewritten to
  parse and compare major.minor with an actionable `Podman >= 5.4 required (found
  …)` message.
- **`eng/build/build.sh`** and **`eng/deploy/setup.sh`** (build-host preflights)
  already parsed major.minor for the buildah minimum; bumped from `4.4` to `5.4`
  literals so the whole project speaks one Podman number. The new floor subsumes
  the buildah-1.28 / `COPY --parents` minimum, so a valid build host is also a
  valid deploy host. The **Docker** build path is independent of Podman and stays
  at its BuildKit floor, **23.0+** — the build-host requirement reads "docker
  23.0+ **or** podman 5.4+".

**Drop the `--add-host` workaround.** With the floor at 5.4,
`host.containers.internal` resolves natively on the rootless `spring-net` bridge,
so the deferred per-container `--add-host=host.containers.internal:host-gateway`
change is unnecessary and is not made. `deploy.sh` is unchanged. (The agent-image
`ContainerConfigBuilder` may keep its `host.docker.internal:host-gateway` mapping
as harmless parity; it is no longer load-bearing.)

## Consequences

**Easier:**

- A rootless deploy on any supported distro brings the stack up with
  worker→dispatcher over `host.containers.internal` working out of the box — no
  workaround, no per-container flag, one networking story.
- One Podman number across install, build, and deploy, anchored in this record;
  the buildah / `COPY --parents` minimum is subsumed rather than tracked
  separately.

**Harder / costs:**

- Hosts on Podman 4.x or 5.0–5.3 are no longer supported and must upgrade; the
  gates fail fast with a message naming 5.4. This is the deliberate trade — the
  excluded versions either carry the bug (4.x, via slirp4netns + no bridge fix) or
  predate the 5.3 fix (5.0–5.3).
- The fixture podman stub (`test-install.sh`) had to rise to a ≥5.4 version so the
  happy-path install test clears the new gate.

**Deferred / tracked:**

- The `podman pull --policy missing` exit-code workaround in
  `ProcessContainerRuntime` is scoped to **podman 4.9.x**, a version this floor now
  excludes. Whether the quirk is absent on 5.x is unverified here (the change can't
  be exercised on a 5.x host locally), so the workaround is left in place and its
  removal is tracked as a follow-up rather than forced into this record.

**Not abstracted (deliberately):**

- No runtime feature-detection of pasta / the bridge fix — a single declared
  version floor enforced at install and build time is simpler and matches how the
  rest of the toolchain (bash, dotnet, docker) is gated.
