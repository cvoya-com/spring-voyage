# Units

A **unit** is an agent with children. It receives messages and runs through the
same mailbox, execution config, runtime launcher, inheritance, and orchestration
tool model described in [Agents](agents.md). This page covers only the
unit-specific layer added on top of the agent primitive.

## Children

Units are hierarchical containers. A unit can contain leaf agents and other
units, so teams can be composed recursively. The children list is the structural
difference between a unit and a leaf agent.

## Membership and permissions

Membership records define which agents or sub-units belong to a unit. Humans
participate through permission grants on the unit:

| Role | Permissions |
| --- | --- |
| Owner | Configure the unit, manage members, set policies, and delete. |
| Operator | Start, stop, interact, approve workflow steps, and view operational state. |
| Viewer | Read state, activity, metrics, and agent status. |

Permissions can flow through the hierarchy unless a boundary or explicit grant
changes the view.

## Lifecycle workflow

Units have their own lifecycle workflow: creation, validation, activation,
operation, suspension, teardown, and soft deletion. Validation checks runtime,
connector bindings, credentials, image, and membership shape before the unit is
ready.

## Expertise aggregation

A unit aggregates expertise from its children. The directory can expose raw
child expertise, projected expertise, or synthesised team-level capabilities
depending on the unit boundary. This lets a parent ask what the unit can do
without every member detail.

## Boundary

A unit boundary controls what the parent can see when the unit is used as a
child:

| Level | What the parent sees |
| --- | --- |
| Transparent | Child members, expertise, and activity are visible. |
| Translucent | A filtered or projected subset is visible. |
| Opaque | The unit appears as a single agent. |

Boundary rules project, filter, synthesise, or aggregate the internal view.
Deep access is still permission-gated by the membership graph; the boundary is a
default presentation, not an identity wall.

## Connector binding

Connectors bind external systems to units. A GitHub, Slack, or Figma connector
translates external events into messages addressed to the unit and exposes
connector skills to agents working inside that unit. Connector configuration is
owned by the binding; unit membership and boundary rules decide which children
can observe or act on the resulting work.
