// What Compare tests, from the measures the query shows NOW (UAT 17 Oct, P27 = F31). The Original formula used to be set once, from the
// first measure the page ever saw, and kept: remove Average Order Value, add Net Sales, and Compare still tested [Average Order Value]
// against the person's rewrite of Net Sales, reporting "a difference in 12 combinations" that was a comparison of two different numbers.
// Here the original is read from the query as it is, every time. A formula the person typed is kept only while it is for the measure
// that is still shown.

export interface CompareOriginal {
  /** The measures the query shows now, as DAX references ("[Net Sales]"), in the order shown. Two or more: the person picks one. */
  choices: string[];
  /** The measure Compare tests: the person's pick while it is still shown, else the first one shown. */
  chosen: string;
  /** The formula in the Original box: the person's own text for this measure, else the measure itself. */
  text: string;
}

/** `shown` are the measures of the query on screen; `pick` the one the person chose; `typed` the text they wrote, and the measure it was written for. */
export function compareOriginal(shown: readonly string[], pick: string | null | undefined, typed: { forRef: string; text: string } | null | undefined): CompareOriginal {
  const choices = [...new Set(shown.filter(Boolean))];
  const chosen = pick && choices.includes(pick) ? pick : choices[0] ?? '';
  const text = typed && typed.forRef === chosen ? typed.text : chosen;
  return { choices, chosen, text };
}

/** The one plain line said when the only caution is a calculation group and a formula does read the measure's name. */
export const calcCautionLine = 'The numbers matched, but a calculation group could change what a formula that reads the measure name returns.';

const CALC_GROUP_FUNCTION = /\b(?:ISSELECTEDMEASURE|SELECTEDMEASURE\w*)\b/i;

/** Whether the calculation-group caution belongs on this comparison (UAT 18 Oct, N11: SUM(Sales[NetSalesAmount]) against [Net Sales] matched
 *  in every combination, and the verdict said "Not proven" with a paragraph on ISSELECTEDMEASURE that neither formula uses). It does only when
 *  the caution is about calculation groups AND a formula reads the measure's identity. With no formulas to read, it stays. Any other caution stays. */
export function calcCautionApplies(fidelity: string | null | undefined, requestKey: string | null | undefined): boolean {
  if (!fidelity || !/calculation[- ]group/i.test(fidelity)) return true;
  let formulas: unknown;
  try { formulas = requestKey ? JSON.parse(requestKey) : null; } catch { return true; }
  if (!Array.isArray(formulas) || typeof formulas[0] !== 'string' || typeof formulas[1] !== 'string') return true;
  return CALC_GROUP_FUNCTION.test(formulas[0]) || CALC_GROUP_FUNCTION.test(formulas[1]);
}

/** The verdict for a real difference, said plainly (pro UAT 18 Oct, N5: "Not proven; do not apply" with words about reduced fidelity). */
export function differenceLine(differing: number, compared: number): string {
  // The engine counts the totals it compares along with the finest combinations, so the differing count can pass the compared one: no "of" then.
  if (differing > compared) return `Different in ${differing} combinations. Do not apply.`;
  return compared === 1 ? 'Different in the only combination. Do not apply.' : `Different in ${differing} of ${compared} combinations. Do not apply.`;
}

/** A number from the comparison as a person reads it: sixteen decimals become four, thousands get commas. A word is left alone. */
export function shownNumber(v: string, formatString?: string | null): string {
  const t = v.trim();
  if (!/^-?\d+(\.\d+)?(E[+-]?\d+)?$/i.test(t)) return v;
  // A percent measure reads as a percent (UAT 18 Oct, round 3, R10: 0.3583 and 0.239 for 35.8% and 23.9%), with the places its format has.
  const percent = /^([^%]*)%/.exec(formatString ?? '');
  if (percent) return `${(Number(t) * 100).toFixed(/\.([0#]+)/.exec(percent[1])?.[1].length ?? 0)}%`;
  return Number(t).toLocaleString('en-US', { maximumFractionDigits: 4 });
}

/** The format the REWRITE's numbers read in. The original's percent format carries over only when the rewrite is a ratio (it divides), or is a bare measure
 *  that has a format of its own: a SUM beside a percent measure is an amount, and reading it as 7524945711.0% would show a wrong number as right. */
export function rewriteFormat(rewrite: string, originalFormat: string | null | undefined, formatOfMeasure: (name: string) => string | null | undefined): string | null | undefined {
  const text = (rewrite ?? '').trim();
  const bare = /^\[((?:[^\]]|\]\])+)\]$/.exec(text);
  if (bare) return formatOfMeasure(bare[1].replace(/\]\]/g, ']'));
  const shape = text.replace(/"(?:[^"]|"")*"/g, '""').replace(/'(?:[^']|'')+'/g, "''").replace(/\[(?:[^\]]|\]\])+\]/g, '[]');
  return /\bDIVIDE\s*\(|\//i.test(shape) ? originalFormat : undefined;
}

/** A combination as the engine names it ("Product[ProductName]=Trail Tent") in the words a person uses ("Product name: Trail Tent"). */
export function plainCombination(c: string): string {
  const words = (col: string) => { const w = col.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/_/g, ' '); return w.charAt(0).toUpperCase() + w.slice(1).toLowerCase(); };
  return c.replace(/(?:'[^']+'|\w+)\[([^\]]+)\]=/g, (_m, col: string) => `${words(col)}: `);
}
