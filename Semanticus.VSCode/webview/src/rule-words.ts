// A rule's name as a person reads it. The shipped rule names start with the rule's category in square brackets
// ("[Formatting] Do not summarize numeric columns"). The category is already a heading or a chip on its own page, so the bracket
// is engine shorthand and is not shown (copy21). Pure and idempotent, so a name can pass through it twice.

/** The categories the engine puts in front of a rule name in square brackets: the built-in Best Practice Analyzer ones, and the readiness ones. */
const BUILT_IN_TAGS = new Set([
  'performance', 'formatting', 'dax expressions', 'maintenance', 'naming conventions', 'error prevention',
  'naming', 'descriptions', 'synonyms', 'relationships', 'visibility', 'copilot limits', 'data agent config', 'data agent', 'best practice',
]);

/** Size findings carry the engine's storage terms in their names; the page says them plainly (UAT round 2, N19). */
const STORAGE_NAMES: Record<string, string> = {
  'Dictionary-dominated column': 'Column with many different values',
  'Hash-index-heavy column': 'Column with a large lookup part',
};

/** "[Formatting] Do not summarize numeric columns" becomes "Do not summarize numeric columns". Only a built-in category is taken off: a custom
 *  rule named "[Sales Amount] needs a format string" keeps its name, because its bracket is a field. A name that is only a bracket stays as it is. */
export function plainRuleName<T extends string | null | undefined>(name: T): T {
  if (typeof name !== 'string') return name;
  const storage = STORAGE_NAMES[name];
  if (storage) return storage as T;
  const tag = /^\s*\[([^\]\r\n]{1,40})\]\s+/u.exec(name);
  if (!tag || !BUILT_IN_TAGS.has(tag[1].trim().toLowerCase())) return name;
  const plain = name.slice(tag[0].length);
  return (plain.length > 0 ? plain : name) as T;
}

/** A readiness category as a person reads it: the engine's type names ("CopilotLimits", "DataAgentConfig") become words (UAT round 2, N25). */
export function plainCategory<T extends string | null | undefined>(name: T): T {
  if (typeof name !== 'string') return name;
  if (name === 'CopilotLimits') return 'Copilot limits' as T;
  if (name === 'DataAgentConfig') return 'Data agent setup' as T;
  return name;
}

/** The note under the readiness grade, in plain words. The engine's wording ("capped at D", ">50% ...") stays on the wire and in MCP; the card says it
 *  plainly. A note this does not know passes through as it came. */
export function plainGate(note: string): string {
  const objects = /^(\d+) objects? holds? a formula that refers to an object the model does not have; capped at D$/u.exec(note);
  if (objects) return `${objects[1]} ${objects[1] === '1' ? 'object has' : 'objects have'} a formula that points at something the model does not have. The grade stops at D.`;
  if (note === '>50% of visible measures undescribed; capped at D') return 'More than half of your measures have no description. The grade stops at D.';
  if (note === 'Copilot scale ceiling exceeded; capped at D') return 'The model is bigger than Copilot handles well. The grade stops at D.';
  return note;
}
