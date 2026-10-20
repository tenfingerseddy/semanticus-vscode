// The ticked fields of the object browser, in the order they were ticked. A tick belongs to the field, not to what the search
// shows now: the search changing must not drop it (UAT 18 Oct, round 3, R3: tick Brand, search "year", tick Year, Brand was lost).
export function tickedNodes<N extends { ref: string }>(nodes: readonly N[], ticked: ReadonlySet<string>, alreadyPicked?: ReadonlySet<string>): N[] {
  const byRef = new Map(nodes.map((n) => [n.ref, n] as const));
  return [...ticked].map((ref) => byRef.get(ref)).filter((n): n is N => !!n && !alreadyPicked?.has(n.ref));
}
