# Units

## What a unit is

A **unit** is an [agent](agents.md) that owns children.

This page covers only the unit-specific layer. Shared concepts such as mailbox
intake, execution config, runtime invocation, inheritance, and the platform
messaging tools live in [Agents](agents.md). For the at-a-glance reference of what
applies to both unit and leaf agent vs only one, see
[Units vs agents](units-vs-agents.md).

## Children

Units compose leaf agents, sub-units, and human team members. All three are
declared on the unit's `members:` list with the unified [ADR-0046](../decisions/0046-unified-members-grammar.md)
grammar — `- agent:`, `- unit:`, or `- human:`:

```yaml
members:
  - agent: ada
  - unit: { from: engineering, name: engineering-1 }
  - human:
      roles: [owner, security_lead]
      expertise: [security, infra]
      notifications: [escalation, completion]
```

After install, the membership graph is editable through the API surface:

- Member agents are assigned through `POST /api/v1/tenant/units/{id}/agents/{agentId}`.
- Sub-units are added through `PUT /api/v1/tenant/units/{unitId}/memberships/{agentAddress}`.
- Human team members are added through the unit-membership endpoints introduced
  alongside [ADR-0046](../decisions/0046-unified-members-grammar.md); the
  `unit_memberships_humans` row carries `roles`, `expertise`, and `notifications`
  jsonb columns keyed by `(tenant, unit, human)`.

Agent and unit membership rows carry the same multi-valued `roles` /
`expertise` jsonb columns ([ADR-0046 §8](../decisions/0046-unified-members-grammar.md));
the fields are runtime metadata surfaced through the `sv.directory.list_members`
directory tool, not platform-decision inputs.

The full member list is exposed to the runtime through `sv.directory.list_members`
(see [`SvDirectorySkillRegistry`](../../src/Cvoya.Spring.Dapr/Skills/SvDirectorySkillRegistry.cs)).

## Permissions

A unit owns human permission grants, the expertise scope it exposes, and the
expertise boundary enforced by `UnitBoundary`. Human grants control who can
configure, operate, or view the unit. Declared expertise plus boundary rules
govern what outside callers can see and which issues or work the unit is
eligible to receive.

Human *team-role* membership (declared on the package YAML's `members:` block)
is orthogonal to *platform-role* permissions
([ADR-0044 §1](../decisions/0044-team-role-vs-platform-role.md), preserved
unchanged by [ADR-0046](../decisions/0046-unified-members-grammar.md)). A
declaration on `members:` does not grant any platform authority; ACL grants
stay on `unit_human_permissions` and are managed through the existing
`/api/v1/tenant/units/{id}/humans/{humanId}/permissions` surface. See
[Humans](humans.md) for the two-axes model.

## Lifecycle workflow

The unit lifecycle runs from creation to membership and then active operation:

1. Create the unit and persist its identity, execution config, optional
   connector binding, boundary, and own expertise.
2. Add member agents, sub-units, and human team members (either declaratively
   via the package YAML's `members:` block or imperatively via the membership
   endpoints).
3. Validate runtime, credentials, connector binding, image, and membership
   shape.
4. Activate the unit so domain messages can invoke its runtime.

## Connector binding

A unit can be bound to a connector such as GitHub, Arxiv, or Web Search. The
binding stores connector-specific configuration and credentials, translates
external events into messages for the unit, and contributes connector skills to
runtime tool discovery. Packages declare the requirement with `requires: [ { connector: <slug> } ]`
on the unit's `package.yaml`; the install pipeline asks the operator for one
binding per unique requirement. See [Connectors](connectors.md) for the binding
model.

## Expertise aggregation

When a unit has children, its expertise is the union of the children's declared
expertise plus any expertise declared directly on the unit. Boundary rules can
project, filter, or synthesise that aggregate so callers outside the unit see
the unit-level capability rather than every internal detail.
