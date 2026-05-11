"use client";

import { AlertTriangle } from "lucide-react";

import { translateApiError } from "@/lib/api/translate-error";

export function ApiErrorMessage({ error }: { error: unknown }) {
  const translated = translateApiError(error);
  const hasDetails = Boolean(
    translated.details?.traceId || translated.details?.raw,
  );

  return (
    <div
      role="alert"
      className="rounded-md border border-destructive/50 bg-destructive/10 px-3 py-2 text-sm text-destructive"
      data-testid="api-error-message"
    >
      <div className="flex items-start gap-2">
        <AlertTriangle
          className="mt-0.5 h-4 w-4 shrink-0"
          aria-hidden="true"
        />
        <div className="min-w-0 flex-1">
          <p className="font-medium">{translated.title}</p>
          {translated.nextStep && (
            <p className="mt-1 text-destructive/90">{translated.nextStep}</p>
          )}
          {hasDetails && (
            <details className="mt-2 text-xs text-muted-foreground">
              <summary className="cursor-pointer select-none text-foreground underline-offset-2 hover:underline">
                Show details
              </summary>
              <div className="mt-2 space-y-2">
                {translated.details?.traceId && (
                  <p>
                    <span className="font-medium text-foreground">
                      Trace id:
                    </span>{" "}
                    <code className="break-all rounded bg-muted px-1 py-0.5 font-mono text-[11px] text-foreground">
                      {translated.details.traceId}
                    </code>
                  </p>
                )}
                {translated.details?.raw && (
                  <pre className="max-h-48 overflow-auto rounded-md bg-muted p-2 font-mono text-[11px] leading-relaxed text-foreground">
                    {translated.details.raw}
                  </pre>
                )}
              </div>
            </details>
          )}
        </div>
      </div>
    </div>
  );
}
