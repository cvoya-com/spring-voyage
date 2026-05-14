# syntax=docker/dockerfile:1.7
#
# tests/fixtures/byoi-path2/Dockerfile — smoke fixture for BYOI
# conformance path 2 (#1120). Exercises the historical npm-install
# shape of path 2: the smoke driver runs `npm pack` against the
# in-tree src/Cvoya.Spring.AgentSidecar/ sources and `npm i -g`s the
# resulting tarball below.
#
# The @cvoya/spring-voyage-agent-sidecar npm package is no longer
# published to npmjs.org as of 2026-05-13. The supported path-2 install
# method going forward is the SEA binary attached to each GitHub
# Release — see docs/guide/operator/byoi-agent-images.md path 2 for
# the canonical recipe. This fixture is retained so the historical
# npm-pack install path remains exercised in CI as long as the
# sidecar source supports it.
#
# Build context: tests/fixtures/byoi-path2/. The smoke script
# (tests/smoke/smoke-1087.sh, --path 2 mode) drops the tarball into
# this directory ahead of `docker build`, then deletes it on exit.

# node:22-alpine matches the `engines.node: ">=22"` declared in
# src/Cvoya.Spring.AgentSidecar/package.json. Alpine keeps the image small
# and is intentionally a different base than the path-1 agent-base
# image — the whole point of path 2 is to prove the bridge can be
# dropped into an arbitrary Node-bearing distro. Pinned to an exact
# patch so smoke runs are reproducible.
FROM docker.io/library/node:22.22.2-alpine

# The npm-pack tarball name is `<scope-flattened>-<version>.tgz`
# (npm's algorithm, not ours), so for @cvoya/spring-voyage-agent-sidecar
# at 1.0.0 it's `cvoya-spring-voyage-agent-sidecar-1.0.0.tgz`. We accept
# the filename via build-arg so a sidecar version bump only requires
# updating the smoke script, not this Dockerfile.
ARG SIDECAR_TARBALL=cvoya-spring-voyage-agent-sidecar-1.0.0.tgz

WORKDIR /tmp/sidecar
COPY ${SIDECAR_TARBALL} ./sidecar.tgz

# Install the bridge globally. npm symlinks the binary declared in the
# package's `bin` field (`spring-voyage-agent-sidecar`) into
# /usr/local/bin/, where the ENTRYPOINT below picks it up. Drop the
# tarball afterwards so it doesn't bloat the image layer.
RUN npm install -g ./sidecar.tgz \
 && rm -f /tmp/sidecar/sidecar.tgz

WORKDIR /

# Default A2A port from src/Cvoya.Spring.AgentSidecar/src/config.ts. The
# smoke driver publishes this on a random host port via `-p`, mirrors
# the path-1 contract.
EXPOSE 8999

# `bin.spring-voyage-agent-sidecar` from the sidecar package.json. This
# IS the path-2 conformance contract: the bridge is the image's PID 1
# (well, with `node` as the actual exec'd binary, but functionally PID 1
# — Alpine doesn't ship tini and we deliberately don't add it here so
# the smoke is a faithful "minimal Node-bearing image" fixture; the
# real-world recipe in docs/guide/byoi-agent-images.md mentions tini
# only as an option).
ENTRYPOINT ["spring-voyage-agent-sidecar"]
