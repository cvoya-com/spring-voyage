"use client";

// Agent overrides panel (Settings hub / #1744). Surfaces agent-scoped
// secrets — the narrowest tier of the resolver chain
// (Agent → Unit → ParentUnit → Tenant; LlmCredentialResolver). Use this
// to override a tenant default or a unit-scoped secret for a single
// agent without touching the broader scopes.
//
// Shape note vs. tenant-defaults-panel.tsx:
//  - Tenant defaults render a fixed-list of "known" credentials
//    (anthropic-oauth, anthropic-api-key, openai-api-key, google-api-key)
//    since every unit inherits from there and the at-a-glance set/unset
//    matrix is the primary use-case.
//  - Agent overrides are per-agent and arbitrary-named — operators set
//    whatever name the agent's runtime expects, and the panel never
//    needs to know the catalog. The form mirrors the unit Secrets tab's
//    add/list/delete shape (free-form name input) rather than the
//    fixed-row tenant matrix.
//
// Propagation: agent-scope secrets have no descendants, so the
// `propagate` toggle the unit Secrets tab carries (#1741) is
// deliberately omitted. The `--propagate` CLI flag is unit-only by
// design; the server ignores `propagate` at agent scope.

import { useCallback, useEffect, useMemo, useState } from "react";
import { KeyRound, Plus, Trash2 } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import type { AgentResponse, SecretMetadata } from "@/lib/api/types";

type AddMode = "value" | "externalStoreKey";

export function AgentOverridesPanel() {
  const { toast } = useToast();

  // Agent directory + selection
  const [agents, setAgents] = useState<AgentResponse[]>([]);
  const [agentsLoading, setAgentsLoading] = useState(true);
  const [agentsError, setAgentsError] = useState<string | null>(null);
  const [selectedAgentId, setSelectedAgentId] = useState<string>("");
  const [agentFilter, setAgentFilter] = useState<string>("");

  // Per-agent secrets
  const [secrets, setSecrets] = useState<SecretMetadata[] | null>(null);
  const [secretsLoading, setSecretsLoading] = useState(false);
  const [secretsError, setSecretsError] = useState<string | null>(null);

  // Add-secret form
  const [addMode, setAddMode] = useState<AddMode>("value");
  const [newName, setNewName] = useState("");
  const [newValue, setNewValue] = useState("");
  const [newExternalKey, setNewExternalKey] = useState("");
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  const [deletingName, setDeletingName] = useState<string | null>(null);

  // Initial agent list
  useEffect(() => {
    let cancelled = false;
    setAgentsLoading(true);
    void api
      .listAgents()
      .then((list) => {
        if (cancelled) return;
        setAgents(list);
        setAgentsError(null);
      })
      .catch((err) => {
        if (cancelled) return;
        setAgentsError(err instanceof Error ? err.message : String(err));
        setAgents([]);
      })
      .finally(() => {
        if (cancelled) return;
        setAgentsLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, []);

  const refreshSecrets = useCallback(
    async (agentId: string) => {
      try {
        const list = await api.listAgentSecrets(agentId);
        setSecrets(list.secrets ?? []);
        setSecretsError(null);
      } catch (err) {
        setSecretsError(err instanceof Error ? err.message : String(err));
        setSecrets([]);
      }
    },
    [],
  );

  // Reload secret list whenever the operator picks a different agent
  useEffect(() => {
    if (!selectedAgentId) {
      setSecrets(null);
      setSecretsError(null);
      return;
    }
    let cancelled = false;
    setSecretsLoading(true);
    refreshSecrets(selectedAgentId).finally(() => {
      if (!cancelled) setSecretsLoading(false);
    });
    return () => {
      cancelled = true;
      // Best-effort: zero out any plaintext held in state when the
      // active agent changes — same hygiene as the unit Secrets tab.
      setNewValue("");
      setNewExternalKey("");
    };
  }, [selectedAgentId, refreshSecrets]);

  // Filtered agent list — typeahead. The match is case-insensitive
  // across the human-facing displayName and the address-bound name so
  // operators can narrow with whichever they have at hand.
  const filteredAgents = useMemo(() => {
    const q = agentFilter.trim().toLowerCase();
    if (!q) return agents;
    return agents.filter((a) => {
      const dn = (a.displayName || "").toLowerCase();
      const nm = (a.name || "").toLowerCase();
      return dn.includes(q) || nm.includes(q);
    });
  }, [agents, agentFilter]);

  const selectedAgent = useMemo(
    () => agents.find((a) => a.name === selectedAgentId) ?? null,
    [agents, selectedAgentId],
  );

  const resetForm = () => {
    setNewName("");
    setNewValue("");
    setNewExternalKey("");
    setSubmitError(null);
  };

  const handleAdd = async () => {
    if (!selectedAgentId) return;
    setSubmitError(null);

    if (!newName.trim()) {
      setSubmitError("Name is required.");
      return;
    }
    if (addMode === "value" && !newValue) {
      setSubmitError("Value is required.");
      return;
    }
    if (addMode === "externalStoreKey" && !newExternalKey.trim()) {
      setSubmitError("External store key is required.");
      return;
    }

    setSubmitting(true);
    try {
      // No propagate field — agent scope has no descendants and the
      // server ignores the flag here (#1741). Sending it would only
      // muddy operator intent.
      await api.createAgentSecret(selectedAgentId, {
        name: newName.trim(),
        value: addMode === "value" ? newValue : undefined,
        externalStoreKey:
          addMode === "externalStoreKey"
            ? newExternalKey.trim()
            : undefined,
      });
      toast({ title: "Agent override added", description: newName.trim() });
      resetForm();
      await refreshSecrets(selectedAgentId);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      setSubmitError(message);
      toast({
        title: "Add failed",
        description: message,
        variant: "destructive",
      });
    } finally {
      setSubmitting(false);
    }
  };

  const handleDelete = async (name: string) => {
    if (!selectedAgentId) return;
    setDeletingName(name);
    try {
      await api.deleteAgentSecret(selectedAgentId, name);
      toast({ title: "Agent override cleared", description: name });
      await refreshSecrets(selectedAgentId);
    } catch (err) {
      const message = err instanceof Error ? err.message : String(err);
      toast({
        title: "Delete failed",
        description: message,
        variant: "destructive",
      });
    } finally {
      setDeletingName(null);
    }
  };

  return (
    <div className="space-y-4" data-testid="settings-agent-overrides">
      <p className="text-xs text-muted-foreground">
        Per-agent secret overrides. Values set here win over unit-scoped
        and tenant-default secrets for the chosen agent only — useful
        when one agent needs a different LLM key, a sandboxed tool
        token, or any other narrowed credential. Values are stored
        server-side and never returned to the browser.
      </p>

      {agentsError && (
        <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-xs text-destructive">
          {agentsError}
        </p>
      )}

      <div className="space-y-2">
        <label className="block space-y-1">
          <span className="text-xs text-muted-foreground">Agent</span>
          <Input
            type="search"
            value={agentFilter}
            onChange={(e) => setAgentFilter(e.target.value)}
            placeholder="Filter agents…"
            autoComplete="off"
            spellCheck={false}
            className="text-xs"
            data-testid="agent-overrides-filter"
            disabled={agentsLoading || agents.length === 0}
          />
        </label>
        <select
          value={selectedAgentId}
          onChange={(e) => setSelectedAgentId(e.target.value)}
          aria-label="Agent"
          disabled={agentsLoading || filteredAgents.length === 0}
          className="flex h-9 w-full rounded-md border border-input bg-background px-3 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring disabled:cursor-not-allowed disabled:opacity-50"
          data-testid="agent-overrides-agent-select"
        >
          <option value="">
            {agentsLoading
              ? "Loading agents…"
              : agents.length === 0
                ? "No agents available"
                : filteredAgents.length === 0
                  ? "No agents match the filter"
                  : "Pick an agent…"}
          </option>
          {filteredAgents.map((a) => (
            <option key={a.name} value={a.name}>
              {a.displayName ? `${a.displayName} (${a.name})` : a.name}
            </option>
          ))}
        </select>
      </div>

      {!selectedAgentId ? (
        <p
          className="rounded-md border border-dashed border-border px-3 py-3 text-xs text-muted-foreground"
          data-testid="agent-overrides-empty-state"
        >
          Pick an agent above to view and manage its overrides. Operators
          who prefer the CLI can use{" "}
          <code className="font-mono text-[11px]">
            spring secret --scope agent --agent &lt;id&gt;
          </code>
          .
        </p>
      ) : (
        <div className="space-y-3" data-testid="agent-overrides-secrets">
          <div className="flex items-center gap-2">
            <KeyRound className="h-3.5 w-3.5 text-muted-foreground" />
            <span className="text-xs font-medium">
              {selectedAgent?.displayName || selectedAgentId}
            </span>
            <Badge variant="outline" className="text-[10px]">
              {(secrets ?? []).length} override
              {(secrets ?? []).length === 1 ? "" : "s"}
            </Badge>
          </div>

          {secretsError && (
            <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-xs text-destructive">
              {secretsError}
            </p>
          )}

          {secretsLoading ? (
            <p className="text-xs text-muted-foreground">Loading…</p>
          ) : (secrets ?? []).length === 0 ? (
            <p className="text-xs text-muted-foreground">
              No agent-scope overrides registered. The agent inherits
              every secret from its unit, parent unit, and tenant.
            </p>
          ) : (
            <ul
              className="divide-y divide-border rounded-md border border-border"
              data-testid="agent-overrides-list"
            >
              {(secrets ?? []).map((s) => (
                <li
                  key={s.name}
                  className="flex items-center gap-3 px-3 py-2"
                  data-testid={`agent-override-row-${s.name}`}
                >
                  <span className="font-mono text-xs">{s.name}</span>
                  <Badge variant="outline" className="text-[10px]">
                    set on agent
                  </Badge>
                  <span className="ml-auto text-[10px] text-muted-foreground">
                    {new Date(s.createdAt).toLocaleString()}
                  </span>
                  <Button
                    size="sm"
                    variant="outline"
                    onClick={() => handleDelete(s.name)}
                    disabled={deletingName === s.name}
                    aria-label={`Delete ${s.name}`}
                  >
                    <Trash2 className="h-3 w-3" />
                  </Button>
                </li>
              ))}
            </ul>
          )}

          <div className="space-y-2 rounded-md border border-border p-3">
            <div className="flex items-center gap-2">
              <Plus className="h-3.5 w-3.5 text-muted-foreground" />
              <span className="text-xs font-medium">Add override</span>
            </div>

            <div className="flex gap-2">
              <Button
                size="sm"
                variant={addMode === "value" ? "default" : "outline"}
                onClick={() => setAddMode("value")}
                disabled={submitting}
              >
                Pass-through value
              </Button>
              <Button
                size="sm"
                variant={addMode === "externalStoreKey" ? "default" : "outline"}
                onClick={() => setAddMode("externalStoreKey")}
                disabled={submitting}
              >
                External reference
              </Button>
            </div>

            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">Name</span>
              <Input
                value={newName}
                onChange={(e) => setNewName(e.target.value)}
                placeholder="anthropic-api-key"
                autoComplete="off"
                className="text-xs"
                data-testid="agent-override-name"
              />
            </label>

            {addMode === "value" ? (
              <label className="block space-y-1">
                <span className="text-xs text-muted-foreground">
                  Value (stored server-side; never returned)
                </span>
                <Input
                  type="password"
                  value={newValue}
                  onChange={(e) => setNewValue(e.target.value)}
                  autoComplete="off"
                  spellCheck={false}
                  className="text-xs"
                  data-testid="agent-override-value"
                />
              </label>
            ) : (
              <label className="block space-y-1">
                <span className="text-xs text-muted-foreground">
                  External store key
                </span>
                <Input
                  value={newExternalKey}
                  onChange={(e) => setNewExternalKey(e.target.value)}
                  placeholder="kv://vault/secret-id"
                  autoComplete="off"
                  className="text-xs"
                  data-testid="agent-override-external-key"
                />
              </label>
            )}

            {submitError && (
              <p className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-xs text-destructive">
                {submitError}
              </p>
            )}

            <div className="flex justify-end">
              <Button
                size="sm"
                onClick={() => {
                  void handleAdd();
                }}
                disabled={submitting}
                data-testid="agent-override-submit"
              >
                {submitting ? "Adding…" : "Add override"}
              </Button>
            </div>
          </div>
        </div>
      )}

      <p className="text-[11px] text-muted-foreground">
        Agent overrides are the narrowest tier of the resolver chain
        (Agent → Unit → ParentUnit → Tenant). Need bulk operations or
        cross-agent diffs? Use{" "}
        <code className="font-mono text-[11px]">
          spring secret list --scope agent --agent &lt;id&gt;
        </code>
        .
      </p>
    </div>
  );
}
