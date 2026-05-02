"use client";

/**
 * /packages/[name] — package detail view (#395 / PR-PLAT-PKG-1).
 *
 * Section breakdown matches the CLI's `spring package show <name>`
 * output verbatim: unit templates, agent templates, skills, connectors,
 * workflows. Every unit template row carries a "Show" link into the
 * template detail page so the operator can preview the YAML that
 * `spring package install` would consume.
 *
 * Install button (ADR-0035 #1565): clicking Install opens a dialog where
 * the operator supplies key/value inputs, then submits to
 * POST /api/v1/packages/install. On success the browser redirects to
 * /installs/<id> to follow Phase-2 progress.
 */

import Link from "next/link";
import { useRouter } from "next/navigation";
import {
  ArrowRight,
  Download,
  FileText,
  Layers,
  Package as PackageIcon,
  Puzzle,
  Users,
  Wrench,
  X,
  Plus,
  Trash2,
} from "lucide-react";
import { useState, type ReactNode } from "react";

import { Breadcrumbs } from "@/components/breadcrumbs";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Dialog } from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { usePackage } from "@/lib/api/queries";
import { useInstallPackages } from "@/lib/api/queries";

interface Props {
  name: string;
}

/** One key/value input row in the inputs form. */
interface InputRow {
  key: string;
  value: string;
}

export default function PackageDetailClient({ name }: Props) {
  const query = usePackage(name);
  const pkg = query.data;
  const router = useRouter();

  const [installOpen, setInstallOpen] = useState(false);
  const [inputRows, setInputRows] = useState<InputRow[]>([]);
  const [submitError, setSubmitError] = useState<string | null>(null);

  const installMutation = useInstallPackages();

  function openInstall() {
    setInputRows([]);
    setSubmitError(null);
    setInstallOpen(true);
  }

  function closeInstall() {
    if (installMutation.isPending) return;
    setInstallOpen(false);
    setSubmitError(null);
  }

  function addInputRow() {
    setInputRows((prev) => [...prev, { key: "", value: "" }]);
  }

  function removeInputRow(index: number) {
    setInputRows((prev) => prev.filter((_, i) => i !== index));
  }

  function updateInputRow(index: number, field: "key" | "value", value: string) {
    setInputRows((prev) =>
      prev.map((row, i) => (i === index ? { ...row, [field]: value } : row)),
    );
  }

  async function handleInstallSubmit(e: React.FormEvent) {
    e.preventDefault();
    setSubmitError(null);

    const inputs: Record<string, string> = {};
    for (const row of inputRows) {
      const k = row.key.trim();
      const v = row.value.trim();
      if (k) {
        inputs[k] = v;
      }
    }

    try {
      const result = await installMutation.mutateAsync([
        { packageName: name, inputs },
      ]);
      setInstallOpen(false);
      router.push(`/installs/${result.installId}`);
    } catch (err) {
      setSubmitError(
        err instanceof Error ? err.message : "Install failed. Please try again.",
      );
    }
  }

  if (query.isPending) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-8 w-48" />
        <Skeleton className="h-48" />
      </div>
    );
  }

  if (query.error) {
    return (
      <div className="space-y-4">
        <Breadcrumbs
          items={[
            { label: "Packages", href: "/settings/packages" },
            { label: name },
          ]}
        />
        <Card>
          <CardContent className="p-6">
            <p className="text-sm text-destructive" role="alert">
              Failed to load package: {query.error.message}
            </p>
          </CardContent>
        </Card>
      </div>
    );
  }

  if (pkg === null || pkg === undefined) {
    return (
      <div className="space-y-4">
        <Breadcrumbs
          items={[
            { label: "Packages", href: "/settings/packages" },
            { label: name },
          ]}
        />
        <Card>
          <CardContent className="p-6">
            <p className="text-sm text-muted-foreground">
              Package &quot;{name}&quot; not found.
            </p>
          </CardContent>
        </Card>
      </div>
    );
  }

  // Counts shown in the header let the operator see the same summary
  // the /packages list card surfaced; rendering them here keeps the
  // two pages coherent when the user deep-links straight to detail.
  const counts = [
    { label: "Unit templates", value: pkg.unitTemplates?.length ?? 0 },
    { label: "Agent templates", value: pkg.agentTemplates?.length ?? 0 },
    { label: "Skills", value: pkg.skills?.length ?? 0 },
    { label: "Connectors", value: pkg.connectors?.length ?? 0 },
    { label: "Workflows", value: pkg.workflows?.length ?? 0 },
  ];

  return (
    <>
      <div className="space-y-6">
        <Breadcrumbs
          items={[
            { label: "Packages", href: "/settings/packages" },
            { label: pkg.name ?? name },
          ]}
        />

        <div className="flex items-start justify-between gap-4">
          <div>
            <h1 className="flex items-center gap-2 text-2xl font-bold">
              <PackageIcon className="h-5 w-5" /> {pkg.name}
            </h1>
            {pkg.description && (
              <p className="mt-1 text-sm text-muted-foreground">
                {pkg.description}
              </p>
            )}
            <div className="mt-3 flex flex-wrap gap-1.5">
              {counts.map((c) => (
                <Badge
                  key={c.label}
                  variant={c.value === 0 ? "outline" : "secondary"}
                >
                  {c.value} {c.label}
                </Badge>
              ))}
            </div>
          </div>

          {/* Install action — primary CTA for this page */}
          <Button
            onClick={openInstall}
            className="shrink-0"
            aria-label={`Install package ${pkg.name ?? name}`}
            data-testid="install-button"
          >
            <Download className="mr-2 h-4 w-4" aria-hidden="true" />
            Install
          </Button>
        </div>

        <Section
          title="Unit templates"
          icon={<Layers className="h-4 w-4" />}
          count={pkg.unitTemplates?.length ?? 0}
        >
          {(pkg.unitTemplates ?? []).map((t) => (
            <div
              key={`${t.package}/${t.name}`}
              className="flex items-start justify-between rounded border border-border p-3 text-sm"
            >
              <div className="min-w-0 flex-1">
                <p className="font-medium">{t.name}</p>
                {t.description && (
                  <p className="mt-1 text-xs text-muted-foreground">
                    {t.description}
                  </p>
                )}
                {t.path && (
                  <p className="mt-1 truncate text-xs text-muted-foreground">
                    {t.path}
                  </p>
                )}
              </div>
              <Link
                href={`/settings/packages/${encodeURIComponent(pkg.name ?? "")}/templates/${encodeURIComponent(t.name ?? "")}`}
                className="ml-3 inline-flex items-center gap-1 text-xs text-primary hover:underline"
                aria-label={`Show template ${t.name}`}
              >
                Show <ArrowRight className="h-3 w-3" />
              </Link>
            </div>
          ))}
        </Section>

        <Section
          title="Agent templates"
          icon={<Users className="h-4 w-4" />}
          count={pkg.agentTemplates?.length ?? 0}
        >
          {(pkg.agentTemplates ?? []).map((a) => (
            <div
              key={`${a.package}/${a.name}`}
              className="rounded border border-border p-3 text-sm"
            >
              <div className="flex items-start justify-between gap-2">
                <p className="font-medium">
                  {a.displayName ?? a.name}{" "}
                  <span className="text-xs font-normal text-muted-foreground">
                    ({a.name})
                  </span>
                </p>
                {a.role && <Badge variant="secondary">{a.role}</Badge>}
              </div>
              {a.description && (
                <p className="mt-1 text-xs text-muted-foreground">
                  {a.description}
                </p>
              )}
            </div>
          ))}
        </Section>

        <Section
          title="Skills"
          icon={<Wrench className="h-4 w-4" />}
          count={pkg.skills?.length ?? 0}
        >
          {(pkg.skills ?? []).map((s) => (
            <div
              key={`${s.package}/${s.name}`}
              className="flex items-center justify-between rounded border border-border p-3 text-sm"
            >
              <div>
                <p className="font-medium">{s.name}</p>
                {s.path && (
                  <p className="text-xs text-muted-foreground">{s.path}</p>
                )}
              </div>
              {s.hasTools && <Badge variant="outline">tools.json</Badge>}
            </div>
          ))}
        </Section>

        <Section
          title="Connectors"
          icon={<Puzzle className="h-4 w-4" />}
          count={pkg.connectors?.length ?? 0}
        >
          {(pkg.connectors ?? []).map((c) => (
            <div
              key={`${c.package}/${c.name}`}
              className="rounded border border-border p-3 text-sm"
            >
              <p className="font-medium">{c.name}</p>
              {c.path && (
                <p className="text-xs text-muted-foreground">{c.path}</p>
              )}
            </div>
          ))}
        </Section>

        <Section
          title="Workflows"
          icon={<FileText className="h-4 w-4" />}
          count={pkg.workflows?.length ?? 0}
        >
          {(pkg.workflows ?? []).map((w) => (
            <div
              key={`${w.package}/${w.name}`}
              className="rounded border border-border p-3 text-sm"
            >
              <p className="font-medium">{w.name}</p>
              {w.path && (
                <p className="text-xs text-muted-foreground">{w.path}</p>
              )}
            </div>
          ))}
        </Section>
      </div>

      {/* Install dialog */}
      <Dialog
        open={installOpen}
        onClose={closeInstall}
        title={`Install ${pkg.name ?? name}`}
        description="Supply any input values required by the package, then click Install. Leave the list empty if the package declares no inputs."
        footer={
          <>
            <Button
              variant="outline"
              onClick={closeInstall}
              disabled={installMutation.isPending}
              type="button"
            >
              Cancel
            </Button>
            <Button
              type="submit"
              form="install-inputs-form"
              disabled={installMutation.isPending}
              data-testid="install-submit-button"
            >
              {installMutation.isPending ? "Installing…" : "Install"}
            </Button>
          </>
        }
      >
        <form id="install-inputs-form" onSubmit={handleInstallSubmit} noValidate>
          {inputRows.length > 0 && (
            <div
              className="mb-3 space-y-2"
              role="list"
              aria-label="Package inputs"
            >
              {inputRows.map((row, i) => (
                <div
                  key={i}
                  className="flex items-center gap-2"
                  role="listitem"
                >
                  <Input
                    placeholder="Key"
                    value={row.key}
                    onChange={(e) => updateInputRow(i, "key", e.target.value)}
                    aria-label={`Input key ${i + 1}`}
                    className="flex-1"
                  />
                  <Input
                    placeholder="Value"
                    value={row.value}
                    onChange={(e) => updateInputRow(i, "value", e.target.value)}
                    aria-label={`Input value ${i + 1}`}
                    className="flex-1"
                  />
                  <Button
                    type="button"
                    variant="ghost"
                    size="icon"
                    onClick={() => removeInputRow(i)}
                    aria-label={`Remove input ${i + 1}`}
                  >
                    <Trash2 className="h-4 w-4" aria-hidden="true" />
                  </Button>
                </div>
              ))}
            </div>
          )}

          <Button
            type="button"
            variant="outline"
            size="sm"
            onClick={addInputRow}
            className="w-full"
          >
            <Plus className="mr-2 h-3.5 w-3.5" aria-hidden="true" />
            Add input
          </Button>

          {submitError && (
            <p
              className="mt-3 text-sm text-destructive"
              role="alert"
              data-testid="install-error"
            >
              {submitError}
            </p>
          )}
        </form>
      </Dialog>
    </>
  );
}

function Section({
  title,
  icon,
  count,
  children,
}: {
  title: string;
  icon: ReactNode;
  count: number;
  children: ReactNode;
}) {
  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center gap-2 text-base">
          {icon}
          {title} ({count})
        </CardTitle>
      </CardHeader>
      <CardContent>
        {count === 0 ? (
          <p className="text-sm text-muted-foreground">(none)</p>
        ) : (
          <div className="space-y-2">{children}</div>
        )}
      </CardContent>
    </Card>
  );
}
