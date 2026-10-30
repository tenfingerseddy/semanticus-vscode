import { gapShort, plainCheckError, relationshipTally } from './tests-model';

export interface ChecksMeasure {
  ref: string; name: string; description?: string; oneLine?: string; descriptionProvenance?: string;
  descriptionMatch?: { score: number | null; level: string; label: string; reason?: string };
  table?: string; folder?: string;
}
export interface ChecksSummary {
  total: number;
  numbersDiffer: number; keyProblems: number; tableProblems: number; attention?: boolean; status?: string;
}
export interface TrustedNumber { id: string; title: string; targetRef?: string; enabled: boolean; kind: string; }
export interface NumberResult { targetRef?: string; verdict: string; actual?: string; expected?: string; message?: string; difference?: number; }
export interface ChecksTable { modelTable: string; lastModelCount?: number; lastSourceCount?: number; lastVerdict?: string; }
export interface ChecksPageView {
  sessionId?: string; revision?: number; measures: ChecksMeasure[]; summary: ChecksSummary;
  numbers: { definitions: TrustedNumber[] }; results: Record<string, NumberResult>; tables: { rows: ChecksTable[] };
  relationships?: { relationships: { name: string; dataTypeMatch: { verdict: string }; keyUniqueness: { verdict: string }; referentialIntegrity: { verdict: string } }[] } | null;
  when?: string;
  resultTimes?: Record<string, string>; relationshipsWhen?: string;
  /** What stops scoring now ("Pro advice is off for this model."), from the engine's scoring state; null when nothing does. */
  unscored?: string | null;
}
export function numberReason(result?: NumberResult, enabled = true): string {
  if (!enabled) return 'Off';
  if (!result) return 'Not run yet. Runs with Run checks';
  if (result.verdict === 'Pass') return `Same${result.actual ? `: ${result.actual}` : ' as your number'}`;
  if ((result.verdict === 'Fail' || result.verdict === 'Suspect') && result.difference != null)
    return `${result.expected?.trim().startsWith('$') ? '$' : ''}${gapShort(result.difference, undefined, undefined, result.expected)} than your number`;
  if (result.verdict === 'Fail' || result.verdict === 'Suspect') return result.actual != null && result.expected != null
    ? `Shows ${result.actual}, your number is ${result.expected}` : result.message || 'Your number differs';
  const why = plainCheckError(result.message);
  if (/evaluator|engine version|view-as-role/i.test(why)) return 'This kind of saved check cannot run yet';
  if (/offline|no live connection/i.test(why)) return 'Connect the model to run this check';
  return why;
}

export function relationshipsReason(relationships: NonNullable<ChecksPageView['relationships']>['relationships']): { reason: string; tone: string } {
  if (!relationships.length) return { reason: 'Not checked yet', tone: 'not_checked' };
  const tally = relationshipTally(relationships.map(r => [r.dataTypeMatch.verdict, r.keyUniqueness.verdict, r.referentialIntegrity.verdict]));
  if (tally.attention) {
    const onlyMissingKeys = relationships.every(r => ![r.dataTypeMatch, r.keyUniqueness].some(c => /^(Fail|Suspect)$/.test(c.verdict)));
    return { reason: `${tally.attention} of ${relationships.length} ${onlyMissingKeys ? `${tally.attention === 1 ? 'has' : 'have'} keys with no match` : `${tally.attention === 1 ? 'needs' : 'need'} attention`}`, tone: 'wrong' };
  }
  if (tally.unknown) return { reason: `${tally.unknown} could not be checked`, tone: 'not_checked' };
  return { reason: `All ${relationships.length} match`, tone: 'checked' };
}
