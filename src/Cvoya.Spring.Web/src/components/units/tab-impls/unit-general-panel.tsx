"use client";

// Unit Config → General sub-tab (#2331). First tab in the Config strip;
// surfaces every directory-owned metadata field that previously could
// only be set at create time (displayName, description, model hint,
// color) plus the existing expertise editor folded in.

import { useMemo, useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { Settings2 } from "lucide-react";

import { UnitExpertisePanel } from "@/components/expertise/unit-expertise-panel";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { useToast } from "@/components/ui/toast";
import { api } from "@/lib/api/client";
import { useUnit } from "@/lib/api/queries";
import { queryKeys } from "@/lib/api/query-keys";
import { formatTranslatedError } from "@/lib/api/translate-error";

interface UnitGeneralPanelProps {
  unitId: string;
}

interface DraftFields {
  displayName: string;
  description: string;
  model: string;
  color: string;
}

const EMPTY_DRAFT: DraftFields = {
  displayName: "",
  description: "",
  model: "",
  color: "",
};

export function UnitGeneralPanel({ unitId }: UnitGeneralPanelProps) {
  const { toast } = useToast();
  const queryClient = useQueryClient();
  const unitQuery = useUnit(unitId);

  const persisted: DraftFields = useMemo(() => {
    const data = unitQuery.data;
    return data
      ? {
          displayName: data.displayName ?? "",
          description: data.description ?? "",
          model: data.model ?? "",
          color: data.color ?? "",
        }
      : EMPTY_DRAFT;
  }, [unitQuery.data]);

  const [draft, setDraft] = useState<DraftFields>(EMPTY_DRAFT);
  const [seededFor, setSeededFor] = useState<string | null>(null);
  const fingerprint = `${unitId}:${persisted.displayName}:${persisted.description}:${persisted.model}:${persisted.color}`;

  // Render-phase derived-state pattern (same shape <InstructionsPanel> uses)
  // so the textarea seeds from the latest persisted snapshot without paying
  // a cascading effect render. React optimizes setState during render when
  // it matches an existing setter on this component.
  if (fingerprint !== seededFor) {
    setDraft(persisted);
    setSeededFor(fingerprint);
  }

  const dirty =
    draft.displayName !== persisted.displayName ||
    draft.description !== persisted.description ||
    draft.model !== persisted.model ||
    draft.color !== persisted.color;

  const saveMutation = useMutation({
    mutationFn: async () => {
      const patch: Partial<{
        displayName: string;
        description: string;
        model: string;
        color: string;
      }> = {};
      if (draft.displayName !== persisted.displayName) {
        patch.displayName = draft.displayName;
      }
      if (draft.description !== persisted.description) {
        patch.description = draft.description;
      }
      if (draft.model !== persisted.model) {
        patch.model = draft.model;
      }
      if (draft.color !== persisted.color) {
        patch.color = draft.color;
      }
      await api.updateUnit(unitId, patch);
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: queryKeys.units.detail(unitId) });
      queryClient.invalidateQueries({ queryKey: queryKeys.directory.all });
      toast({ title: "Unit details saved" });
    },
    onError: (err) => {
      toast({
        title: "Save failed",
        description: formatTranslatedError(err),
        variant: "destructive",
      });
    },
  });

  if (unitQuery.isPending) {
    return <Skeleton className="h-64" data-testid="unit-general-skeleton" />;
  }

  return (
    <div className="space-y-4" data-testid="unit-general-panel">
      <Card>
        <CardHeader className="flex flex-row items-center gap-2 space-y-0 pb-2">
          <CardTitle className="flex items-center gap-2 text-base">
            <Settings2 className="h-4 w-4" />
            <span>General</span>
          </CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          <p className="text-xs text-muted-foreground">
            Core metadata for this unit. Each field maps 1:1 to a field on{" "}
            <code className="rounded bg-muted px-1 py-0.5 text-xs">
              spring unit update
            </code>
            .
          </p>

          <label className="block space-y-1">
            <span className="text-xs text-muted-foreground">Display name</span>
            <Input
              data-testid="unit-general-display-name"
              value={draft.displayName}
              onChange={(e) =>
                setDraft((d) => ({ ...d, displayName: e.target.value }))
              }
            />
          </label>

          <label className="block space-y-1">
            <span className="text-xs text-muted-foreground">Description</span>
            <textarea
              data-testid="unit-general-description"
              className="min-h-[96px] w-full rounded-md border border-input bg-background p-2 text-sm shadow-sm focus-visible:outline-none focus-visible:ring-1 focus-visible:ring-ring"
              value={draft.description}
              onChange={(e) =>
                setDraft((d) => ({ ...d, description: e.target.value }))
              }
            />
          </label>

          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">
                Model (hint)
              </span>
              <Input
                data-testid="unit-general-model"
                value={draft.model}
                placeholder="e.g. claude-3-5-sonnet-latest"
                onChange={(e) =>
                  setDraft((d) => ({ ...d, model: e.target.value }))
                }
              />
            </label>

            <label className="block space-y-1">
              <span className="text-xs text-muted-foreground">Color</span>
              <div className="flex items-center gap-2">
                <input
                  type="color"
                  data-testid="unit-general-color-picker"
                  aria-label="Unit color"
                  className="h-9 w-12 cursor-pointer rounded-md border border-input bg-background p-0.5"
                  value={normaliseHexInput(draft.color)}
                  onChange={(e) =>
                    setDraft((d) => ({ ...d, color: e.target.value }))
                  }
                />
                <Input
                  data-testid="unit-general-color-hex"
                  aria-label="Unit color hex value"
                  className="flex-1"
                  placeholder="#5b8def"
                  value={draft.color}
                  onChange={(e) =>
                    setDraft((d) => ({ ...d, color: e.target.value }))
                  }
                />
              </div>
            </label>
          </div>

          <div className="flex items-center gap-2 pt-2">
            <Button
              onClick={() => saveMutation.mutate()}
              disabled={!dirty || saveMutation.isPending}
              data-testid="unit-general-save"
            >
              {saveMutation.isPending ? "Saving…" : "Save"}
            </Button>
            {dirty && (
              <Button
                variant="outline"
                onClick={() => setDraft(persisted)}
                disabled={saveMutation.isPending}
                data-testid="unit-general-revert"
              >
                Revert
              </Button>
            )}
          </div>
        </CardContent>
      </Card>

      <UnitExpertisePanel unitId={unitId} />
    </div>
  );
}

// The native <input type="color"> picker rejects non-#RRGGBB values silently —
// fall back to a neutral grey when the persisted color is blank or non-hex so
// the swatch still renders something meaningful.
function normaliseHexInput(value: string): string {
  if (/^#[0-9a-fA-F]{6}$/.test(value)) {
    return value;
  }
  return "#888888";
}
