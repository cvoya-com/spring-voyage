# Spring Voyage CLI

`spring` is the command-line client for [Spring Voyage](https://github.com/cvoya-com/spring-voyage),
an AI agent collaboration platform. The CLI talks to a deployed Spring
Voyage installation via its REST API — install once, point at any
deployment.

## Install

```bash
dotnet tool install -g Cvoya.Spring.Cli
spring --help
```

Requires the .NET 10 runtime (matches the version `spring` is built against).
Make sure `~/.dotnet/tools` is on your `PATH` (the `dotnet` SDK installer
typically does this for you).

> **Working on the CLI source?** `dotnet build src/Cvoya.Spring.Cli`
> automatically packs and globally `dotnet tool update`s `spring` from the
> local feed in **Debug** builds, so `spring …` just works after every
> rebuild. Opt out with `/p:InstallSpringCliOnBuild=false`; Release builds
> and CI skip the auto-install.

## Use

Point the CLI at your deployment, then explore from `--help`:

```bash
export SPRING_API_URL=https://your-spring-voyage-host
spring --help
```

A few common entry points:

```bash
spring unit list
spring unit create my-team --tool claude-code
spring secret create --scope tenant anthropic-api-key --value "sk-ant-..."
spring system configuration
```

## Where does the platform come from?

The CLI ships independently of the platform itself. The platform is
installed via the source-free installer:

```bash
curl -fSL https://github.com/cvoya-com/spring-voyage/releases/latest/download/install.sh | bash
```

See the [operator deployment guide](https://github.com/cvoya-com/spring-voyage/blob/main/docs/guide/operator/deployment.md)
for the full walkthrough.

## License

Business Source License 1.1. See
[LICENSE.md](https://github.com/cvoya-com/spring-voyage/blob/main/LICENSE.md)
in the source repository.
