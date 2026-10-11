// The one line under a saved report in the Reports library (UAT N5: "13 of 32 checked" over a report whose own page
// said Broken, because the one check that differed was in neither number).
//
// A summary can never read cleaner than the rows behind it, so it is built from every result state the report keeps:
// passed, needs review, broke (a test that differs, a step that failed), overridden, and could not be checked. Each
// state with something in it is named; none is folded into "checked". An older report with no per-state counts still
// names its own verdict when that verdict is not a clean one, and says how many results it cannot account for.

const plural = (n, one, many) => (n === 1 ? one : many);

/**
 * @param {{ kind?: string; verdict?: string; verified?: number|null; total?: number|null; unknowns?: number|null;
 *           verdictCounts?: Record<string, number>|null }} item
 * @returns {string|null} null only when the report carries no counts and no verdict to say anything about
 */
export function evidenceCountsLine(item) {
  if (!item) return null;
  const counts = item.verdictCounts ?? null;
  const total = item.total ?? null;
  const verified = counts?.Verified ?? item.verified ?? null;
  const unknown = counts?.Unknown ?? item.unknowns ?? 0;
  const review = counts?.NeedsReview ?? 0;
  const broke = counts?.Broken ?? 0;
  const overridden = counts?.Overridden ?? 0;
  if (verified == null && total == null && !item.verdict) return null;

  const parts = [];
  if (verified != null && total != null) parts.push(`${verified} of ${total} passed`);
  else if (verified != null) parts.push(`${verified} passed`);
  // A test that no longer matches reads "differs"; anywhere else a result that broke reads "broken".
  if (broke > 0) parts.push(item.kind === 'test-suite' ? `${broke} ${plural(broke, 'differs', 'differ')}` : `${broke} broken`);
  if (review > 0) parts.push(`${review} ${plural(review, 'needs', 'need')} review`);
  if (overridden > 0) parts.push(`${overridden} overridden`);
  if (unknown > 0) parts.push(`${unknown} could not be checked`);

  // Whatever the counts leave out must still show: results that are neither passed nor named above.
  const named = (verified ?? 0) + broke + review + overridden + unknown;
  if (total != null && verified != null && total > named) {
    const left = total - named;
    parts.push(`${left} ${plural(left, 'result', 'results')} not passed`);
  }
  // A verdict that is not clean with nothing in the counts to explain it (an older file) is still said, in its own word.
  const v = item.verdict;
  const explained = (v === 'Broken' && broke > 0) || (v === 'NeedsReview' && review > 0) || (v === 'Overridden' && overridden > 0)
    || v === 'Verified' || (v === 'Unknown' && unknown > 0) || !v;
  if (!explained) parts.push(v === 'Broken' ? 'Broken' : v === 'NeedsReview' ? 'Needs review' : v === 'Unknown' ? 'Not verified' : v);
  return parts.length ? parts.join(' · ') : null;
}
