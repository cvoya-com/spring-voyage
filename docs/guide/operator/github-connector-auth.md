# GitHub connector: authentication options

> **Audience.** Operators and OSS-platform users deciding how a unit's GitHub connector binding authenticates to a repository — in particular, anyone who wants a unit to work with a public repository they do **not** own.

> **Scope.** The auth *choices* a binding can make and their trade-offs. For the mechanics of registering and installing the per-deployment GitHub App, see [GitHub App setup](github-app-setup.md). For the binding CLI/wizard, see [Per-unit connector binding](../user/units-and-agents.md).

A GitHub connector binding stores a qualified `owner/repo` plus **exactly one** auth field ([ADR-0047](../../decisions/0047-platform-user-human-split.md) §11): a **GitHub App installation** or a **PAT secret**. The two map to fundamentally different GitHub identities, so the choice is not cosmetic — it decides *who* the action is attributed to and *what* it is allowed to do.

## The two identities GitHub gives you

| | **App installation token** | **Personal access token (PAT)** |
|---|---|---|
| Acts as | the **App** — a bot (`your-app[bot]`) | **you** — the token's owner |
| Minted from | the App's private key (server-to-server) | the token value itself |
| Permissions | what the App was granted on that installation | what **your account** can already do on the repo |
| Needs the SV GitHub App? | yes — registered **and** installed | **no** |
| Needs OAuth ("Link GitHub account")? | yes, for the repo picker | **no** |
| Installing on the repo requires | repo/org **admin** | nothing — any account works |

One constraint sits above both and cannot be configured away:

> **GitHub never lets a non-collaborator write to a repository.** On a public repo you are not a collaborator on, the ceiling — App *or* PAT — is: read, open issues, comment, and **fork → pull request**. Pushing branches, merging, labeling, and closing others' issues are collaborator-only.

So the App does not "unlock" more access on a repo you don't own. It only changes the identity to a bot — which matters when you *do* own the repo and want a clean bot actor, and matters a lot if you were thinking of sharing one (see the last section).

## Which to use

### GitHub App installation — repos you own or operate

Best when the deployment controls the repo (your own project, your org). You get a bot identity with exactly the permissions the App was granted, and — on a publicly-reachable deployment — an active webhook for inbound events. This is the path the [OSS dogfooding unit](dogfooding-oss-unit.md) uses against `cvoya-com/spring-voyage`.

Requires: `spring github-app register`, installing the App on the org/repo (admin needed), then binding with the installation ID. Full setup is in [GitHub App setup](github-app-setup.md).

### PAT — repos you don't own (the OSS-contributor path)

When you want a unit to contribute to a public repo you are **not** a maintainer of, you cannot install your App there — so the App path is a dead end, and the repo will never appear in the wizard's installation dropdown. Use a PAT instead. The unit then acts **as you**, with exactly your GitHub permissions: on a public repo that means open issues, comment, and fork → PR. ([ADR-0047](../../decisions/0047-platform-user-human-split.md) establishes "a PAT against a repo without the SV App installed" — its *use case 1* — as a first-class binding shape.)

**This is the simpler install.** For the contributor use case you can skip the GitHub App entirely — no `spring github-app register`, no "Link GitHub account" OAuth, and none of the `https`/`localhost` OAuth-callback setup that comes with the App. The whole GitHub setup is:

```bash
# 1. Create a PAT on github.com scoped to what the unit needs.
#    Public-repo contribution: a classic token with `public_repo`, or a
#    fine-grained token granting the repo permissions the unit uses
#    (Issues / Pull requests: read & write; Contents: read & write for your fork).
# 2. Store it as a tenant secret:
spring secret create --scope tenant github-pat --value 'ghp_...'

# 3. Bind the unit (or pick "Use a PAT secret" in the New Unit wizard and
#    paste the secret name):
spring connector bind --unit <unit-id> --type github --repo owner/repo --pat-secret-name github-pat
```

> The binding's repository field accepts a hand-typed `owner/repo` only on the PAT path. In **App** mode a typed repo has no installation token behind it, so the wizard expects you to pick an App-visible repo from the dropdown instead.

## Webhooks (inbound events) with a PAT

**Yes — inbound events work with a PAT binding.** The webhook handler routes deliveries on `(tenant, owner, repo)`, not on an installation ID ([ADR-0047](../../decisions/0047-platform-user-human-split.md) §10), so a delivery for `owner/repo` lands on a PAT binding exactly as it would on an App binding.

The real gate is **not** the PAT — it's the forwarder. `gh webhook forward` (the local-dev inbound path) registers a short-lived forwarding hook, which requires **repository admin** on the target repo ([local-dev recipe](github-app-setup.md#local-dev-recipe)). Therefore:

- **A repo you admin** (your own project, or your own fork): the forwarder works, and its deliveries route to your PAT binding. You need a `GitHub__WebhookSecret` in `spring.env` — any value; the forwarder signs with it and the platform verifies against the same — but you do **not** need the App. Run it with `voyage gh-webhook-forward`.
- **A repo you don't admin** (a third-party OSS repo): you cannot register a forwarding hook there, so inbound events won't reach you — with a PAT *or* an App. The contributor flow is **outbound-driven**: the agent acts on the repo (opens issues/PRs/comments), and you drive it via `spring message send` / schedules rather than via repo events. (This is independent of the auth choice; it is GitHub's webhook-admin rule.)

## Why not one "well-known SV App" everyone shares?

It's tempting to register a single public "Spring Voyage" App, install it on your repo once, and let every SV user select it. It does not do what it appears to, and it splits into two very different things:

- **Users use its installation token** → they act as the official bot, with the bot's write access. That hands every SV user push/merge/close rights on your repo *as the official identity*. It also requires either distributing the App's private key to every self-hosted install (which effectively publishes it) or running a central service that vends bot tokens to strangers. **Don't.**
- **Each user authorizes the shared App (user-to-server)** → they act as *themselves*, bounded by their own access — so it grants **nothing** beyond what their own PAT would. Its only benefit is sparing users from creating a credential, and even that requires a central Spring-Voyage-operated component to hold the App's client secret and run the OAuth exchange (a self-hosted fleet can't safely share a confidential secret).

So a shared App is not a permissions shortcut; at most it is a **UX** convenience that depends on central infrastructure, and it must be scoped to *act-as-the-user* only — never *act-as-the-bot*. That is a hosted-Spring-Voyage direction, not something available today. Until then, the answer for "any user, a repo they don't own" is: **PAT, acting as the user, fork → PR.**

## Summary

| You want a unit to… | Use | Need the App? | Inbound events? |
|---|---|---|---|
| Operate a repo you own/admin (bot identity, full two-way) | App installation | Yes | Yes — App webhook (public deploy) or `gh webhook forward` |
| Contribute to a public repo you don't own | PAT (acts as you, fork → PR) | No | No (you can't hook a repo you don't admin) — outbound-driven |
| Act on your own fork of an upstream | PAT | No | Yes if you admin the fork + run the forwarder |

## See also

- [GitHub App setup](github-app-setup.md) — registering and installing the per-deployment App, and the `gh webhook forward` local-dev recipe.
- [OSS dogfooding unit](dogfooding-oss-unit.md) — the App-installation path end to end against `cvoya-com/spring-voyage`.
- [ADR-0047 — platform / user / human split](../../decisions/0047-platform-user-human-split.md) — the binding shape (§11), `(owner, repo)` webhook routing (§10), and the PAT-without-App use case.
- [Secrets](secrets.md) — storing and scoping the PAT secret.
