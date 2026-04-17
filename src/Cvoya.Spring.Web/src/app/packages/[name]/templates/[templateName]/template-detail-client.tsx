"use client";

/**
 * /packages/[name]/templates/[templateName] — unit-template detail.
 *
 * Surfaces the raw YAML a user would `spring apply`, plus a "Copy
 * `spring apply` command" affordance per § 5.1 of the portal
 * exploration doc so the UI and CLI stay reachable from each other.
 */

import { useState } from "react";
import { Copy, FileCode } from "lucide-react";

import { Breadcrumbs } from "@/components/breadcrumbs";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Skeleton } from "@/components/ui/skeleton";
import { useToast } from "@/components/ui/toast";
import { useUnitTemplateDetail } from "@/lib/api/queries";

interface Props {
  packageName: string;
  templateName: string;
}

export default function TemplateDetailClient({
  packageName,
  templateName,
}: Props) {
  const query = useUnitTemplateDetail(packageName, templateName);
  const { toast } = useToast();
  const [copied, setCopied] = useState(false);

  const applyCommand = `spring template show ${packageName}/${templateName}`;

  async function copyCommand() {
    try {
      await navigator.clipboard.writeText(applyCommand);
      setCopied(true);
      toast({
        title: "Copied",
        description: applyCommand,
      });
      // Reset the "Copied" affordance after a short beat so repeated
      // clicks still give feedback.
      window.setTimeout(() => setCopied(false), 1500);
    } catch {
      toast({
        title: "Copy failed",
        description: "Unable to copy to clipboard.",
        variant: "destructive",
      });
    }
  }

  if (query.isPending) {
    return (
      <div className="space-y-4">
        <Skeleton className="h-8 w-48" />
        <Skeleton className="h-80" />
      </div>
    );
  }

  const detail = query.data;

  const crumbs = [
    { label: "Packages", href: "/packages" },
    { label: packageName, href: `/packages/${encodeURIComponent(packageName)}` },
    { label: templateName },
  ];

  if (query.error) {
    return (
      <div className="space-y-4">
        <Breadcrumbs items={crumbs} />
        <Card>
          <CardContent className="p-6">
            <p className="text-sm text-destructive" role="alert">
              Failed to load template: {query.error.message}
            </p>
          </CardContent>
        </Card>
      </div>
    );
  }

  if (detail === null || detail === undefined) {
    return (
      <div className="space-y-4">
        <Breadcrumbs items={crumbs} />
        <Card>
          <CardContent className="p-6">
            <p className="text-sm text-muted-foreground">
              Template &quot;{packageName}/{templateName}&quot; not found.
            </p>
          </CardContent>
        </Card>
      </div>
    );
  }

  return (
    <div className="space-y-6">
      <Breadcrumbs items={crumbs} />

      <div className="flex items-start justify-between gap-4">
        <div>
          <h1 className="flex items-center gap-2 text-2xl font-bold">
            <FileCode className="h-5 w-5" /> {detail.name}
          </h1>
          <p className="mt-1 text-sm text-muted-foreground">
            Package: <code>{detail.package}</code>
            {detail.path && (
              <>
                {" · "}
                Path: <code>{detail.path}</code>
              </>
            )}
          </p>
        </div>
        <Button
          size="sm"
          variant="outline"
          onClick={copyCommand}
          data-testid="copy-spring-command"
          aria-label="Copy spring template show command"
        >
          <Copy className="mr-1 h-3.5 w-3.5" />
          {copied ? "Copied" : "Copy CLI"}
        </Button>
      </div>

      <Card>
        <CardHeader>
          <CardTitle className="text-base">Manifest</CardTitle>
        </CardHeader>
        <CardContent>
          <pre className="overflow-x-auto rounded bg-muted p-3 text-xs">
            {detail.yaml}
          </pre>
        </CardContent>
      </Card>
    </div>
  );
}
