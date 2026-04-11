# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.1.0] - 2025-04-10

### Added

- Initial open-source release under BSL 1.1
- Core platform: agents, units, connectors, messaging, and orchestration
- Dapr actor implementations (AgentActor, UnitActor, ConnectorActor, HumanActor)
- AI-orchestrated and Workflow orchestration strategies
- GitHub connector (C#, Octokit.net)
- CLI (`spring` command via System.CommandLine)
- API host (ASP.NET Core) and Worker host (Dapr actor runtime)
- Software engineering domain package (agent templates, skills, workflows)
- Partitioned mailbox with conversation suspension
- Four-layer prompt assembly
- Address resolution with cached directory and event-driven invalidation
- Brain/Hands execution model (hosted + delegated)
