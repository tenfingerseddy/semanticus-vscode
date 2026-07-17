// Type surface for tabbusy.mjs (the storagecalc.d.mts pattern: runtime in .mjs, types here).

export const PRESERVED_SURFACES: string[];

export function busyAffordance(
  busySurfaces: readonly string[],
  activeTab: string,
  activeGroup: string | null,
  tabToGroup: Record<string, string>,
): { tabs: Set<string>; groups: Set<string> };
