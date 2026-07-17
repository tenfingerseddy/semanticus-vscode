// Pure tab-bar busy-affordance mapping for the converted, state-preserving Studio surfaces. A plain ES module
// (the storagecalc.mjs pattern) so the node contract test runs the SAME code the webview ships.

// The Studio surfaces whose in-flight work is PRESERVED across a tab switch (stages 1-2 of tab-state preservation):
// Lineage (cloud + local report analysis), DAX Lab (query/debug/profile/benchmark/plan/verify), and Storage (scan).
// Their running ops must surface on the tab bar while another tab is open. M Code and its Profile are intentionally
// ABSENT: PR #246 defines unmount as a cancellation boundary there, so they own no cross-tab busy affordance and must
// never be added to this list.
export const PRESERVED_SURFACES = ['lineage', 'daxlab', 'stats'];

// Given the surfaces currently running an op, the active tab, the active group, and the tab->group map, decide which
// secondary tabs and which primary group buttons show the subtle busy glyph. Mirrors the existing `unseen` bubbling
// exactly: a surface in the OPEN group marks its (non-active) secondary tab; a surface in another group bubbles to
// that group's button. The active tab itself never shows the glyph — you are on it, already watching live progress.
export function busyAffordance(busySurfaces, activeTab, activeGroup, tabToGroup) {
  const tabs = new Set();
  const groups = new Set();
  for (const s of busySurfaces) {
    if (s === activeTab) continue;   // on the tab: progress/result is already visible inline, no bar affordance
    const g = tabToGroup[s];
    if (g == null) continue;
    if (g === activeGroup) tabs.add(s);   // its group is open → mark the secondary tab
    else groups.add(g);                   // its group is closed → bubble to the primary group button
  }
  return { tabs, groups };
}
