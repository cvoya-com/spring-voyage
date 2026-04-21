"use client";

import type { ComponentType } from "react";

import type {
  NodeKind,
  TabName,
  TabsFor,
  TreeNode,
} from "../aggregate";

/**
 * Standard prop shape every per-tab content component receives. The base
 * shape carries the active node + its breadcrumb path so every tab can
 * render headers, empty-states, and "see all" links without re-querying
 * the tree.
 */
export interface TabContentProps {
  node: TreeNode;
  path: TreeNode[];
}

/**
 * Tab key shape used by the registry: `"<kind>.<tab>"`, e.g. `"Unit.Overview"`.
 * The `(kind, tab)` pair, not the bare tab name, is the key because tab
 * labels like `Overview`, `Activity`, `Messages`, `Memory` are shared across
 * kinds with different content shapes.
 */
export type TabKey = {
  [K in NodeKind]: `${K}.${TabsFor<K>}`;
}[NodeKind];

export function tabKey<K extends NodeKind>(kind: K, tab: TabsFor<K>): TabKey {
  return `${kind}.${tab}` as TabKey;
}

const REGISTRY = new Map<TabKey, ComponentType<TabContentProps>>();

/**
 * Register a tab content component for a given `(kind, tab)` pair.
 *
 * The `TabsFor<K>` bound rejects nonsense pairs (e.g. `("Tenant", "Skills")`)
 * at compile time, so every call site is guaranteed to target a valid slot.
 *
 * Duplicate-registration policy:
 *   - In production: throws. A collision means two modules registered the
 *     same slot, which is a real bug that would otherwise silently mask one
 *     of the components.
 *   - Outside production (dev, test): overwrites with a warning. Fast
 *     Refresh re-imports module top-levels on every edit; keeping the
 *     newest component lets HMR actually refresh tab bodies.
 */
export function registerTab<K extends NodeKind>(
  kind: K,
  tab: TabsFor<K>,
  component: ComponentType<TabContentProps>,
): void {
  const key = tabKey(kind, tab);
  if (REGISTRY.has(key)) {
    if (process.env.NODE_ENV === "production") {
      throw new Error(
        `[units/tabs] duplicate registration for ${key} — two modules registered the same slot`,
      );
    }
    console.warn(
      `[units/tabs] re-registering ${key} (HMR or test re-import; newest component wins)`,
    );
  }
  REGISTRY.set(key, component);
}

/**
 * Look up the component registered for a `(kind, tab)` pair, or `null`
 * when no component has been registered. `<DetailPane>` substitutes its
 * `<TabPlaceholder>` for the `null` case.
 *
 * The signature accepts the full `NodeKind × TabName` product so the
 * runtime dispatch site can pass values narrowed only at the kind level.
 */
export function lookupTab(
  kind: NodeKind,
  tab: TabName,
): ComponentType<TabContentProps> | null {
  return REGISTRY.get(`${kind}.${tab}` as TabKey) ?? null;
}

/** Snapshot of every currently-registered tab key. */
export function registeredTabs(): TabKey[] {
  return Array.from(REGISTRY.keys());
}

/** Test-only escape hatch — clears every registration. */
export function __resetTabRegistryForTesting(): void {
  REGISTRY.clear();
}
