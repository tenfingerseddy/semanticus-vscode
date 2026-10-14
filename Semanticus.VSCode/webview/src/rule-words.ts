// A rule's name as a person reads it. The shipped rule names start with the rule's category in square brackets
// ("[Formatting] Do not summarize numeric columns"). The category is already a heading or a chip on its own page, so the bracket
// is engine shorthand and is not shown (copy21). Pure and idempotent, so a name can pass through it twice.

/** The categories the engine puts in front of a rule name in square brackets: the built-in Best Practice Analyzer ones, and the readiness ones. */
const BUILT_IN_TAGS = new Set([
  'performance', 'formatting', 'dax expressions', 'maintenance', 'naming conventions', 'error prevention',
  'naming', 'descriptions', 'synonyms', 'relationships', 'visibility', 'copilot limits', 'data agent config', 'data agent', 'best practice',
]);

/** "[Formatting] Do not summarize numeric columns" becomes "Do not summarize numeric columns". Only a built-in category is taken off: a custom
 *  rule named "[Sales Amount] needs a format string" keeps its name, because its bracket is a field. A name that is only a bracket stays as it is. */
export function plainRuleName<T extends string | null | undefined>(name: T): T {
  if (typeof name !== 'string') return name;
  const tag = /^\s*\[([^\]\r\n]{1,40})\]\s+/u.exec(name);
  if (!tag || !BUILT_IN_TAGS.has(tag[1].trim().toLowerCase())) return name;
  const plain = name.slice(tag[0].length);
  return (plain.length > 0 ? plain : name) as T;
}
