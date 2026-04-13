# GitHub connector — web subdirectory (placeholder)

Per the design of `IConnectorType`, each connector package ships an
optional `web/` subdirectory that holds its web UI. The canonical
location for the GitHub connector's web UI is therefore **this
directory**.

For the initial landing of the generic connector abstraction we ship the
component from this conceptual location but physically host the file at
`src/Cvoya.Spring.Web/src/connectors/github/connector-tab.tsx` — Next.js
+ Turbopack currently refuses to resolve `node_modules` for files
imported from outside the web project root.

Validation of the cross-project web extension mechanism (including the
relocation of this component into the connector package) is explicitly
tracked as issue **#196**. See also **#195** for the runtime connector
discovery follow-up.
