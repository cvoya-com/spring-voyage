"use client";

// Human × Config tab (#2269 — Portal Wave B; ADR-0046 Phase 4 added
// the General sub-tab for the newly-editable `displayName` /
// `description` Human-entity-level fields).
//
// Three sub-tabs:
//
//   General   — Display name + description editor; mirrors Agent /
//               Unit × Config × General. PATCHes
//               `/api/v1/tenant/humans/{id}` via an inline `useMutation`. Phase
//               4 (ADR-0046) made this the default sub-tab so the most
//               common edit (the operator renaming themselves) is one
//               click in.
//
//   Identity  — connector-identity rows mapping `(connector, user-id)`
//               tuples to this Human, so messages addressed via the
//               external system land on the right `human:<guid>`.
//               Wired against the REST surface introduced by PR #2420
//               (`/api/v1/tenant/humans/{id}/identities`).
//
//   Connector — Read-only summary. Per-Human connector bindings are
//               v0.2 work (#2375) and a per-Human memberships
//               aggregator does not exist yet. v0.1 ships a caveat
//               panel pointing at #2375 + #2452 (the v0.2 portal
//               follow-up). See PR body / brief for the rationale.
//
// URL contract — `?tab=Config&subtab=<name>`. Same shape Tenant /
// Unit / Agent × Config use through the canonical `<ConfigTab>`; the
// Human variant builds an equivalent shell inline because the
// canonical `<ConfigTab>` is typed against Tenant | Unit | Agent
// (Humans lack budgets, secrets, expertise, etc., so widening that
// component to include Human would force kind-guard noise inside
// every panel branch).
//
// "You" hint — when the loaded Human is the currently-authenticated
// caller (same identity match the Overview tab uses), the chrome
// surfaces a "You" badge so the OSS single-operator case matches
// A5's convention.

import {
  useCallback,
  useMemo,
  useState,
  useSyncExternalStore,
} from "react";
import Link from "next/link";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { IdCard, Link2, Plug, Settings2, Trash2, UserRound } from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { ConfirmDialog } from "@/components/ui/confirm-dialog";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import {
  Tabs,
  TabsContent,
  TabsList,
  TabsTrigger,
} from "@/components/ui/tabs";
import { useToast } from "@/components/ui/toast";
import { api, ApiError } from "@/lib/api/client";
import { queryKeys } from "@/lib/api/query-keys";
import { formatTranslatedError } from "@/lib/api/translate-error";
import {
  useCurrentUser,
  useHuman,
} from "@/lib/api/queries";
import type {
  UpdateHumanRequest,
} from "@/lib/api/types";
import {
  dispatchExplorerUrlChange,
  getExplorerSearchSnapshot,
  getServerExplorerSearchSnapshot,
  subscribeExplorerUrl,
} from "@/lib/explorer-url";

import { registerTab, type TabContentProps } from "./index";

// ---------------------------------------------------------------------------
// Sub-tab catalog. General is default-active (ADR-0046 Phase 4 — humans
// now have editable displayName + description, so the most common edit
// is one click in). Identity / Connector follow.
// ---------------------------------------------------------------------------

const HUMAN_SUBTABS = ["General", "Identity", "Connector"] as const;
type HumanSubTab = (typeof HUMAN_SUBTABS)[number];

function parseSubTab(raw: string | null): HumanSubTab {
  const fallback: HumanSubTab = "General";
  if (!raw) return fallback;
  const match = HUMAN_SUBTABS.find((s) => s === raw);
  return match ?? fallback;
}

// ---------------------------------------------------------------------------
// Tab shell. Owns the sub-tab strip + URL writes. Each sub-tab body is
// extracted into its own component for code-splitting and so the
// Identity-tab mutations don't fire while the operator sits on
// Connector.
// ---------------------------------------------------------------------------

function HumanConfigTab({ node }: TabContentProps) {
  if (node.kind !== "Human") return null;

  return <HumanConfigBody humanId={node.id} />;
}

function HumanConfigBody({ humanId }: { humanId: string }) {
  // Same useSyncExternalStore pattern as the canonical ConfigTab — the
  // surface reads sub-tab state from the live URL so deep-links and
  // outer ?tab transitions both round-trip without a navigation.
  const search = useSyncExternalStore(
    subscribeExplorerUrl,
    getExplorerSearchSnapshot,
    getServerExplorerSearchSnapshot,
  );
  const activeSubTab = parseSubTab(new URLSearchParams(search).get("subtab"));

  const setActiveSubTab = useCallback((next: string) => {
    const params = new URLSearchParams(window.location.search);
    params.set("subtab", next);
    const qs = params.toString();
    window.history.replaceState(
      null,
      "",
      qs ? `${window.location.pathname}?${qs}` : window.location.pathname,
    );
    dispatchExplorerUrlChange();
  }, []);

  // The "You" hint applies to the chrome header (mirrors A5 / Overview
  // tab convention). Match by Guid, not by display name — usernames
  // can collide across tenants, ids cannot.
  const meQuery = useCurrentUser();
  const humanQuery = useHuman(humanId);
  const isMe = meQuery.data?.id === humanId;
  const displayName =
    humanQuery.data?.displayName ||
    humanQuery.data?.username ||
    null;

  return (
    <div className="space-y-4" data-testid="tab-human-config">
      <header className="flex items-center gap-2 text-sm text-muted-foreground">
        <IdCard className="h-4 w-4" aria-hidden="true" />
        <span>
          Identity bindings and inbound-routing summary for this human.
          The Identity sub-tab maps connector-native user ids
          (e.g. GitHub login) to <code>human:</code>; the platform uses
          those mappings to deliver messages addressed via external
          systems.
        </span>
        {isMe && displayName ? (
          <Badge
            variant="outline"
            className="ml-auto shrink-0"
            data-testid="tab-human-config-you-hint"
          >
            You · {displayName}
          </Badge>
        ) : null}
      </header>
      <Tabs
        defaultValue={HUMAN_SUBTABS[0]}
        value={activeSubTab}
        onValueChange={setActiveSubTab}
      >
        <TabsList aria-label="Human configuration sections">
          {HUMAN_SUBTABS.map((s) => (
            <TabsTrigger key={s} value={s}>
              {s}
            </TabsTrigger>
          ))}
        </TabsList>
        <TabsContent value="General" className="space-y-2">
          <HumanGeneralSubTab humanId={humanId} />
        </TabsContent>
        <TabsContent value="Identity" className="space-y-2">
          <HumanIdentitySubTab humanId={humanId} />
        </TabsContent>
        <TabsContent value="Connector" className="space-y-2">
          <HumanConnectorSubTab />
        </TabsContent>
      </Tabs>
    </div>
  );
}

// ---------------------------------------------------------------------------
// General sub-tab — Human-entity-level editable metadata (ADR-0046
// Phase 4). Two fields: displayName (text input) + description
// (textarea). Mirrors the Agent × Config × General and Unit × Config ×
// General panels: same Card + label/input layout, same dirty-detection
// + Save + Revert pattern, same `useMutation` + cache-invalidation
// wiring. Surfaces the loading skeleton until the human envelope
// resolves so the form never seeds against empty strings.
// ---------------------------------------------------------------------------

interface HumanGeneralDraft {
  displayName: string;
  description: string;
}

const EMPTY_HUMAN_DRAFT: HumanGeneralDraft = {
  displayName: "",
  description: "",
};

function HumanGeneralSubTab({ humanId }: { humanId: string }) {
  const humanQuery = useHuman(humanId);
  const { toast } = useToast();
  const queryClient = useQueryClient();

  const persisted: HumanGeneralDraft = useMemo(() => {
    const data = humanQuery.data;
    return data
      ? {
          displayName: data.displayName ?? "",
          description: data.description ?? "",
        }
      : EMPTY_HUMAN_DRAFT;
  }, [humanQuery.data]);

  const [draft, setDraft] = useState<HumanGeneralDraft>(EMPTY_HUMAN_DRAFT);
  const [seededFor, setSeededFor] = useState<string | null>(null);
  const fingerprint = `${humanId}:${persisted.displayName}:${persisted.description}`;

  // Render-phase derived-state pattern — same shape <AgentGeneralPanel>
  // and <UnitGeneralPanel> use. React optimizes setState during render
  // when it matches an existing setter, so this seeds without paying a
  // cascading effect render.
  if (fingerprint !== seededFor) {
    setDraft(persisted);
    setSeededFor(fingerprint);
  }

  const dirty =
    draft.displayName !== persisted.displayName ||
    draft.description !== persisted.description;

  const saveMutation = useMutation({
    mutationFn: async () => {
      const patch: UpdateHumanRequest = {};
      if (draft.displayName !== persisted.displayName) {
        patch.displayName = draft.displayName;
      }
      if (draft.description !== persisted.description) {
        patch.description = draft.description;
      }
      await api.updateHuman(humanId, patch);
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({
        queryKey: queryKeys.humans.detail(humanId),
      });
      // Directory cache surfaces the display name on every human chip
      // in the Explorer tree, so refresh it on rename too.
      void queryClient.invalidateQueries({
        queryKey: queryKeys.directory.all,
      });
      toast({ title: "Human details saved" });
    },
    onError: (err) => {
      toast({
        title: "Save failed",
        description: formatTranslatedError(err),
        variant: "destructive",
      });
    },
  });

  if (humanQuery.isPending) {
    return <Skeleton className="h-64" data-testid="human-general-skeleton" />;
  }

  return (
    <Card data-testid="tab-human-config-general">
      <CardHeader className="flex flex-row items-center gap-2 space-y-0 pb-2">
        <CardTitle className="flex items-center gap-2 text-base">
          <Settings2 className="h-4 w-4" aria-hidden="true" />
          <span>General</span>
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        <p className="text-xs text-muted-foreground">
          Core metadata for this human. Each field maps 1:1 to a flag on{" "}
          <code className="rounded bg-muted px-1 py-0.5 text-xs">
            spring human set
          </code>
          .
        </p>

        <label className="block space-y-1">
          <span className="text-xs text-muted-foreground">Display name</span>
          <Input
            data-testid="human-general-display-name"
            value={draft.displayName}
            onChange={(e) =>
              setDraft((d) => ({ ...d, displayName: e.target.value }))
            }
          />
        </label>

        <label className="block space-y-1">
          <span className="text-xs text-muted-foreground">Description</span>
          <textarea
            data-testid="human-general-description"
            className="min-h-[96px] w-full rounded-md border border-input bg-background p-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
            value={draft.description}
            onChange={(e) =>
              setDraft((d) => ({ ...d, description: e.target.value }))
            }
          />
        </label>

        <div className="flex items-center gap-2 pt-2">
          <Button
            onClick={() => saveMutation.mutate()}
            disabled={!dirty || saveMutation.isPending}
            data-testid="human-general-save"
          >
            {saveMutation.isPending ? "Saving…" : "Save"}
          </Button>
          {dirty && (
            <Button
              variant="outline"
              onClick={() => setDraft(persisted)}
              disabled={saveMutation.isPending}
              data-testid="human-general-revert"
            >
              Revert
            </Button>
          )}
        </div>
      </CardContent>
    </Card>
  );
}

// ---------------------------------------------------------------------------
// Identity sub-tab — placeholder for the relocated surface.
//
// ADR-0047 §§ 2, 14: connector-native identity moves off the Human row
// onto the new TenantUser principal. Phase H of the umbrella adds the
// new portal user-identity page consuming the relocated
// /api/v1/tenant/users/{id}/identities routes; v0.1's freezing release
// removes the prior list / add / remove surface here and leaves an
// explanatory note in its place so deep-link visitors land on a clean
// "moved to ..." message instead of a blank panel.
// ---------------------------------------------------------------------------

function HumanIdentitySubTab(_props: { humanId: string }) {
  return (
    <Card data-testid="tab-human-config-identity">
      <CardHeader>
        <CardTitle className="flex items-center gap-2">
          <IdCard className="h-4 w-4" aria-hidden="true" /> Connector
          identities have moved
        </CardTitle>
      </CardHeader>
      <CardContent className="space-y-2 text-sm text-muted-foreground">
        <p>
          Connector-native identities (e.g. GitHub login) now live on the
          tenant-user principal, not the Human row. The portal
          surface is{" "}
          <Link
            href="/settings/user-identity"
            className="underline underline-offset-2"
          >
            Settings → User identity
          </Link>
          .
        </p>
      </CardContent>
    </Card>
  );
}

// ---------------------------------------------------------------------------
// Connector sub-tab — caveat-only for v0.1. Per-Human bindings ship in
// v0.2 (#2375) and the per-Human memberships aggregator is a follow-up
// (#2452). The body links to both so the deferred scope is explicit.
// ---------------------------------------------------------------------------

function HumanConnectorSubTab() {
  return (
    <div
      className="space-y-3 rounded-lg border border-border bg-muted/10 p-6 text-sm"
      data-testid="tab-human-config-connector"
    >
      <div className="flex items-start gap-2">
        <Plug
          className="mt-0.5 h-5 w-5 shrink-0 text-muted-foreground"
          aria-hidden="true"
        />
        <div className="space-y-2">
          <p className="font-medium">Per-Human connector bindings</p>
          <p className="text-xs text-muted-foreground">
            Per-Human connector bindings ship in v0.2 (
            <a
              href="https://github.com/cvoya-com/spring-voyage/issues/2375"
              className="underline"
              target="_blank"
              rel="noreferrer"
            >
              #2375
            </a>
            ). Bindings today are unit-scoped — open the owning unit&apos;s{" "}
            <Badge variant="outline">Config</Badge> →{" "}
            <Badge variant="outline">Connector</Badge> sub-tab to inspect
            or edit them.
          </p>
          <p className="text-xs text-muted-foreground">
            Membership listing on this surface is also deferred — see{" "}
            <a
              href="https://github.com/cvoya-com/spring-voyage/issues/2452"
              className="underline"
              target="_blank"
              rel="noreferrer"
            >
              #2452
            </a>{" "}
            (Wave B portal follow-up). When that ships, the read-only
            summary of units this human is a member of will land here.
          </p>
          <p className="flex items-center gap-1.5 text-xs text-muted-foreground">
            <Link2 className="h-3.5 w-3.5" aria-hidden="true" /> Manage
            inbound routing today via the Identity sub-tab — connector
            user ids mapped to this human resolve <code>human:</code>
            -addressed messages through the right channel.
          </p>
        </div>
      </div>
    </div>
  );
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

function errorMessage(err: unknown): string {
  if (err instanceof ApiError) {
    const detail = err.problem?.detail ?? err.problem?.title;
    if (detail && detail.length > 0) return detail;
  }
  if (err instanceof Error) return err.message;
  return "Unexpected error";
}

registerTab("Human", "Config", HumanConfigTab);
