// Whether the rows on screen are the rows of the question the page is still showing. A question's statement ("This shows Net
// Sales, where Year is 2025.") vouches for the query that answered it, so it may sit over rows only when those rows were
// produced by that exact query. The editor still holding the question's query is not enough: an assistant's run_dax can
// replace the rows while the editor and the question stay as they were (Sol 6.1, 3.2, then delta B3). The one comparison
// the chart already uses to decide which fields describe the rows, kept in one place so the statement cannot disagree.

import { squash } from './daxsquash';

/** The query that produced the result on screen: the one the page recorded, else the one the result names. */
export function producedByOf(res: { query?: string | null } | null | undefined, resQuery: string | null | undefined): string {
  return squash(res ? (resQuery ?? res.query) : '');
}

/** True while the rows on screen (or no rows at all) belong to the question: the editor still holds what the question
 *  wrote, and any rows shown were produced by that very query. Rows that name no query cannot be vouched for. */
export function answerOwnsResult({ askOwns, chipDax, res, resQuery }: {
  askOwns: boolean; chipDax: string; res: { query?: string | null } | null | undefined; resQuery: string | null | undefined;
}): boolean {
  if (!askOwns) return false;
  if (!res) return true;
  const produced = producedByOf(res, resQuery);
  return produced !== '' && produced === squash(chipDax);
}
