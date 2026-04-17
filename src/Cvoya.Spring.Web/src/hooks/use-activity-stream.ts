// The activity-stream hook now lives under `@/lib/stream` so it sits
// next to the Next.js SSE route handler it depends on. This shim
// re-exports the hook from its new home so existing imports under
// `@/hooks/use-activity-stream` keep working (see #437).
//
// New code should import from `@/lib/stream/use-activity-stream`.

export {
  useActivityStream,
  type UseActivityStreamOptions,
} from "@/lib/stream/use-activity-stream";
