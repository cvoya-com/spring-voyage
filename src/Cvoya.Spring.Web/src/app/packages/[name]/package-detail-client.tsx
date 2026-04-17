"use client";

/**
 * /packages/[name] — package detail view (#395 / PR-PLAT-PKG-1).
 *
 * Section breakdown matches the CLI's `spring package show <name>`
 * output verbatim: unit templates, agent templates, skills, connectors,
 * workflows. Every unit template row carries a "Show" link into the
 * template detail page so the operator can preview the YAML that
 * `spring apply` (or the create-unit wizard) would consume.
 */

import Link from "next/link";
import {
  ArrowRight,
  FileText,
  Layers,
  Package as PackageIcon,
  Puzzle,
  Users,
  Wrench,
} from "lucide-react";
import type { ReactNode } from "react";

import { Breadcrumbs } from "@/components/breadcrumbs";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { usePackage } from "@/lib/api/queries";

interface Props {
  name: string;
}

export default function PackageDetailClient({ name }: Props) {
  const query = usePackage(name);
  const pkg = query.data;

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
            { label: "Packages", href: "/packages" },
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
            { label: "Packages", href: "/packages" },
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
    <div className="space-y-6">
      <Breadcrumbs
        items={[
          { label: "Packages", href: "/packages" },
          { label: pkg.name ?? name },
        ]}
      />

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
              href={`/packages/${encodeURIComponent(pkg.name ?? "")}/templates/${encodeURIComponent(t.name ?? "")}`}
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
