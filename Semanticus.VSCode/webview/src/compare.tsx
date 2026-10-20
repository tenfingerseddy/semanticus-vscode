// Wire shapes mirror Semanticus.Engine/Alm/AlmProtocol.cs (camelCased). The Publish page lists a compareModels
// diff between your copy and the destination and sends the ticked refs through applyDiff (publish-changes.tsx).
export interface ModelRef { kind: string; path?: string; gitRef?: string; endpoint?: string; database?: string; authMode?: string; tenantId?: string; label?: string; }
export interface ModelDiffItem { ref: string; objectType: string; name: string; table?: string; action: string; leftText?: string; rightText?: string; matchedByName?: boolean; carriedByRename?: boolean; }
export interface ModelDiff { leftLabel?: string; rightLabel?: string; created: number; updated: number; deleted: number; equal: number; items: ModelDiffItem[]; error?: string; }
export interface ApplyDiffResult { applied: boolean; count: number; appliedRefs: string[]; failedRefs: string[]; target?: string; note?: string; error?: string; confirmToken?: string; restorePointId?: string; }

// A context-bar click seeds the Publish page's two sides: your copy, and the destination. A seed with no destination
// (the attached dataset could not be named) leaves the page on "Choose destination".
export type CompareSeed = { left: ModelRef; right: ModelRef | null; note?: string; nonce: number };

// best-effort: if both texts parse to JSON objects, return a per-key left/right/changed comparison.
export function propertyDelta(leftText?: string, rightText?: string): { key: string; left: string | null; right: string | null; changed: boolean }[] | null {
  const lo = tryParseObject(leftText), ro = tryParseObject(rightText);
  if (lo === undefined && ro === undefined) return null;            // neither is JSON → fall back to text
  const lf = lo ? flatten(lo) : {}, rf = ro ? flatten(ro) : {};
  const keys = [...new Set([...Object.keys(lf), ...Object.keys(rf)])].sort();
  if (keys.length === 0) return null;
  return keys.map((key) => {
    const left = key in lf ? lf[key] : null, right = key in rf ? rf[key] : null;
    return { key, left, right, changed: left !== right };
  });
}
function tryParseObject(s?: string): Record<string, unknown> | undefined {
  if (!s) return undefined;
  const t = s.trim();
  if (!t.startsWith('{')) return undefined;
  try { const v = JSON.parse(t); return v && typeof v === 'object' && !Array.isArray(v) ? v as Record<string, unknown> : undefined; }
  catch { return undefined; }
}
// one level deep; deeper values stringified compactly so a property row stays readable.
function flatten(o: Record<string, unknown>, prefix = ''): Record<string, string> {
  const out: Record<string, string> = {};
  for (const [k, v] of Object.entries(o)) {
    const key = prefix ? prefix + '.' + k : k;
    if (v && typeof v === 'object' && !Array.isArray(v) && Object.keys(v).length <= 6) Object.assign(out, flatten(v as Record<string, unknown>, key));
    else out[key] = typeof v === 'string' ? v : JSON.stringify(v);
  }
  return out;
}
