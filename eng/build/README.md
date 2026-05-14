# eng/build/

Source-build artefacts. Used by contributors who have cloned this repository and
want to produce the platform image and agent images locally (the operator
install flow at `eng/install/` pulls these images from GHCR instead).

| File                           | Purpose                                                                                 |
| ------------------------------ | --------------------------------------------------------------------------------------- |
| `build.sh`                     | One-shot local build: platform image, bundled agent images, dispatcher publish.         |
| `build-agent-images.sh`        | Builds the seven reference agent images (claude-code, gemini, dapr, four oss roles).    |
| `build-sidecar.sh`             | Builds `ghcr.io/cvoya-com/spring-voyage-agent-base:dev` from `src/Cvoya.Spring.AgentSidecar/`.        |
| `Dockerfile`                   | Multi-stage platform image (.NET 10 API/Worker + Web + Dapr CLI).                       |
| `Dockerfile.agent-base`        | A2A bridge sidecar base image — published as `ghcr.io/cvoya-com/spring-voyage-agent-base:<semver>`.   |
| `Dockerfile.agent.*`           | Per-tool agent images layered on top of `agent-base`.                                   |
| `examples/dockerfiles/`        | Starter Dockerfiles operators use to extend the per-tool agent images.                  |

Run `./build.sh` from this directory to produce `localhost/spring-voyage:latest`
plus the seven agent images. See [`eng/deploy/README.md`](../deploy/README.md)
for what to do with the resulting images.
