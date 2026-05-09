# Units

## What a unit is

A **unit** is an [agent](agents.md) that owns children.

This page covers only the unit-specific layer. Shared concepts such as mailbox
intake, execution config, runtime invocation, inheritance, and orchestration
tools live in [Agents](agents.md).

## Children

Units compose leaf agents and sub-units. Member agents are assigned through
`POST /api/v1/tenant/units/{id}/agents/{agentId}`; sub-units are added through
the membership surface at `PUT /api/v1/tenant/units/{unitId}/memberships/{agentAddress}`.
Both paths update the unit's child list, which is the list exposed to the
runtime through `list_children`.

## Permissions

A unit owns human permission grants, the expertise scope it exposes, and the
expertise boundary enforced by `UnitBoundary`. Human grants control who can
configure, operate, or view the unit. Declared expertise plus boundary rules
govern what outside callers can see and which issues or work the unit is
eligible to receive.

## Lifecycle workflow

The unit lifecycle runs from creation to membership and then active operation:

1. Create the unit and persist its identity, execution config, optional
   connector binding, boundary, and own expertise.
2. Add member agents or sub-units.
3. Validate runtime, credentials, connector binding, image, and membership
   shape.
4. Activate the unit so domain messages can invoke its runtime.

## Connector binding

A unit can be bound to a connector such as GitHub, Arxiv, or Web Search. The
binding stores connector-specific configuration and credentials, translates
external events into messages for the unit, and contributes connector skills to
runtime tool discovery. See [Connectors](connectors.md) for the binding model.

## Expertise aggregation

When a unit has children, its expertise is the union of the children's declared
expertise plus any expertise declared directly on the unit. Boundary rules can
project, filter, or synthesise that aggregate so callers outside the unit see
the unit-level capability rather than every internal detail.
