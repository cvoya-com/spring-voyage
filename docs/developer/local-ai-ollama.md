# Local AI with Ollama

Spring Voyage ships [Ollama](https://ollama.com) as a first-class LLM backend.
This document covers why, how, and the trade-offs across deployment shapes.

## Why Ollama

- **No API-key friction.** Contributors can run the full agent loop against a
  local model without provisioning an Anthropic or OpenAI key.
- **Free and offline-capable.** Useful for CI-adjacent workflows, demos on
  air-gapped machines, and anywhere egress to a hosted LLM is undesirable.
- **Realistic.** Exercises the same prompt-assembly and streaming code paths
  that the hosted providers use. Latency and token-emission cadence are
  representative — not identical, but closer than a fake LLM.
- **OpenAI-compatible.** Ollama exposes `/v1/chat/completions` on the same
  port as its native API, so the platform talks to it with the same payload
  shape it uses for hosted OpenAI-compatible providers.

## Quick start

```bash
# 1. Enable Ollama in spring.env
cat >> eng/config/spring.env <<EOF
LanguageModel__Ollama__Enabled=true
LanguageModel__Ollama__DefaultModel=llama3.2:3b
EOF

# 2. Start the stack — deploy.sh launches spring-ollama alongside the rest
cd eng/deploy && ./deploy.sh up

# 3. Watch the model pull complete (first run only)
./deploy.sh logs spring-ollama
```

Once the default model is pulled the platform's next agent turn runs against
Ollama. The `AnthropicProvider` registration is replaced when
`LanguageModel:Ollama:Enabled=true` — there is no simultaneous fallback to a
hosted provider.

## Deployment shapes

| Shape                              | GPU support       | Platform config                                        |
| ---------------------------------- | ----------------- | ------------------------------------------------------ |
| OSS single-host, container, CPU    | none              | default — just set `Enabled=true`                      |
| OSS single-host, container, NVIDIA | Linux / WSL2      | `OLLAMA_GPU=nvidia` in `spring.env`                    |
| OSS single-host, host-installed    | macOS Metal       | `deploy.sh up --local-ollama` or `OLLAMA_MODE=host` + `BaseUrl=host.containers.internal` |
| Cloud, multi-tenant, shared        | provider-specific | cloud host pre-registers `IOptions<OllamaOptions>`     |
| Cloud, multi-tenant, per-tenant VM | NVIDIA GPU VM     | cloud host maps tenant → VM endpoint                   |

## GPU support — feasibility matrix

Container-based GPU acceleration is currently the ideal path: uniform across
operating systems, integrated with the deploy script. It is only fully
available on Linux and WSL2 today.

| Host OS              | Container GPU                            | Host-install GPU        |
| -------------------- | ---------------------------------------- | ----------------------- |
| Linux + NVIDIA       | yes (nvidia-container-toolkit)           | yes                     |
| Windows + NVIDIA     | yes (WSL2 + nvidia-container-toolkit)    | yes (native Windows)    |
| Windows + AMD        | limited; depends on ROCm support         | varies                  |
| macOS + Apple Silicon| **no** — Metal does not pass into Podman | yes (`brew install ollama`) |
| macOS + Intel        | n/a (no GPU path worth wiring)           | CPU only                |

Because Metal cannot be exposed inside a rootless container, macOS operators
who want GPU acceleration must run Ollama on the host and point the platform
at `http://host.containers.internal:11434`. The one-shot deploy path is:

```bash
ollama serve &
./eng/deploy/deploy.sh up --local-ollama
```

That flag verifies `http://127.0.0.1:11434/api/tags`, skips the
`spring-ollama` container, removes a stale one if a previous deploy used
container mode, and injects the host-mode Ollama settings into the platform
containers. Use `--ollama-port <port>` or
`--ollama-endpoint <host-or-url>` when the local service is not on the
default port.

The persistent env-file path remains `OLLAMA_MODE=host` with
`LanguageModel__Ollama__BaseUrl=http://host.containers.internal:11434`.

The Spring Voyage agent runtime reaches Ollama through its per-launch Dapr
Conversation sidecar, not directly through the worker's provider. Host mode
therefore generates a delegated-agent component profile under
`~/.spring-voyage/deployment/dapr/components/delegated-spring-voyage-agent-local-ollama`
(or under `SPRING_DEPLOY_STATE_DIR` when set) and rewrites `llm-ollama.yaml`
to use the OpenAI-compatible host endpoint, such as
`http://host.containers.internal:11434/v1`. The requested model still comes
from each unit/agent launch via `SPRING_MODEL`; host mode does not try to
guess models at deployment time. `deploy.sh clean` removes that generated
profile.

## Platform probe behaviour

On startup the Worker and API hosts run `OllamaHealthCheck`, which issues a
single `GET /api/tags` against the configured `BaseUrl`.

- `RequireHealthyAtStartup=false` (default): a failed probe logs a warning
  and the host keeps starting. The first provider call after Ollama comes up
  will succeed; calls made before then fail with `SpringException`.
- `RequireHealthyAtStartup=true`: a failed probe aborts host startup. Use
  this in production deployments where Ollama is a hard dependency and
  you'd rather crash-loop than serve 5xx.

The timeout is controlled by `HealthCheckTimeoutSeconds` (default: 5). The
probe is deliberately cheap; it does not validate that the default model is
pulled.

## Cloud deployment patterns

The private Spring Voyage Cloud repo extends the OSS platform via DI. For
Ollama specifically:

- **Shared backend, per-tenant base URL.** The cloud host pre-registers an
  `IOptionsMonitor<OllamaOptions>` that resolves the `BaseUrl` from the
  authenticated tenant context (e.g. a per-tenant shared pool of Ollama VMs).
  Because `AddCvoyaSpringOllamaLlm` uses `TryAdd`-friendly registrations,
  no OSS code change is required.
- **Per-tenant dedicated GPU VM.** Provision a GPU VM per tenant on demand
  and store the VM's base URL alongside the tenant record. The tenant-aware
  `OllamaOptions` resolver returns the right URL for the current request.
- **Container-based GPU on cloud VMs.** Where the host OS supports it
  (currently Linux), prefer the container path — it inherits the same
  `OLLAMA_GPU=nvidia` plumbing used in OSS. The cloud bootstrap image runs
  `deploy.sh` on its VM startup.

Nothing in this repo assumes single-tenancy, so the cloud layering is
additive: no forks, no monkey-patching.

## Default model

`llama3.2:3b` is the default. It is small enough to fit on contributor
laptops (3.2 GB model weights + overhead) and capable enough to drive the
policy-block and human↔agent scenarios with deterministic-enough responses.

To use a different model:

```bash
# spring.env
OLLAMA_DEFAULT_MODEL=qwen2.5:7b   # or llama3.1:8b, mistral:7b, etc.
LanguageModel__Ollama__DefaultModel=qwen2.5:7b
```

The platform does not need to know every possible unit or agent model at
deployment time. For `spring-voyage` agents using `provider: ollama`, the
agent runtime checks the selected `SPRING_MODEL` before the first LLM turn and
uses Ollama's native `/api/pull` endpoint if the model is missing. Set
`SPRING_OLLAMA_AUTO_PULL=false` in the agent environment to opt out; in that
case, pull manually with `podman exec spring-ollama ollama pull qwen2.5:7b`
or `ollama pull qwen2.5:7b` for host mode.

## Troubleshooting

### "Ollama health check could not reach http://spring-ollama:11434/api/tags"

The container hasn't come up yet, or networking is wedged. Check:

```bash
podman ps --filter name=spring-ollama
podman logs spring-ollama
podman exec spring-worker curl -sSf http://spring-ollama:11434/api/tags
```

### "model 'llama3.2:3b' not found"

The agent runtime normally pulls missing Ollama models on first use. If you see
this error, either the running agent image predates auto-pull, auto-pull was
disabled with `SPRING_OLLAMA_AUTO_PULL=false`, or Ollama could not pull the
requested model name. Pull manually or update the unit/agent model:

```bash
podman exec spring-ollama ollama pull llama3.2:3b
```

### macOS host-install, platform can't reach host

`host.containers.internal` resolves on Podman 4.4+. Verify:

```bash
podman run --rm docker.io/alpine sh -c 'getent hosts host.containers.internal'
```

If that fails, upgrade Podman. As a fallback use the host's LAN IP.

### Connection refused from Tier 1 screening provider

Tier 1 (`Tier1CognitionProvider`) has its own `Initiative:Tier1:OllamaBaseUrl`
knob — unrelated to the primary provider. Both can point at the same Ollama
server; they use different endpoints (`/api/generate` vs `/v1/chat/completions`).

## Related

- `eng/deploy/README.md` — container topology, ports, volumes.
- `eng/config/spring.env.example` — full configuration reference.
- `docs/architecture/components.md` — Dapr components, infrastructure dependencies.
- `src/Cvoya.Spring.Dapr/Execution/OllamaProvider.cs` — reference implementation.
