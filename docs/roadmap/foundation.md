# Foundation: Documentation + UX Exploration

> **[Roadmap Index](README.md)** | **Status: In progress**

Parallel workstream that runs alongside all phases. Two tracks:

- **Track A: Documentation** — must complete before agents start Phase 4 feature work so they have accurate context. Agents update docs as they go.
- **Track B: UX/Design Exploration** — informs portal implementation in Phase 4+. Doesn't block CLI-first feature work.

## Track A: Documentation

- [ ] Architecture docs: refresh for shipped features (#397) — A2A flows, persistent hosting, policy framework, secrets stack
- [ ] User guide: messaging (#398) — `docs/guide/messaging.md` is a stub
- [ ] User guide: secrets management for operators (#399)
- [ ] User guide: deployment — standalone / Docker Compose / Podman (#400)
- [ ] User guide: web portal walkthrough (#401)
- [ ] Reference e2e test scenarios as usage examples (#402)
- [ ] Release and versioning strategy + CHANGELOG.md (#403)
- [ ] Convention: agents update docs alongside feature work (#405)

## Track B: Testing & UX

- [ ] Expand e2e integration tests (#404) — messaging, orchestration, policy, cost, connectors
- [ ] UX/design exploration (#406) — web portal experience for standalone and hosted SV

**Principle:** Agents must update documentation when shipping features. This convention is formalized in #405.
