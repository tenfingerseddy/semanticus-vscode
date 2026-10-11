// Which benchmark result the Performance pane may show as timing. A plain ES module so the node test
// (test/dax-lab-result.test.mjs) runs the same code the webview ships, the diagramfit.mjs pattern.

/** The result to show as measured timing, or null when there is none to show. A result that carries an error,
 *  or that ran zero times, measured nothing: its zeros are not a speed, so the pane must not print them as one
 *  (UAT-S05b-1-002, where a query that never ran read as "under 1 ms"). */
export function measuredBench(result) {
  if (!result || result.error) return null;
  if (!(result.runs > 0) || !Array.isArray(result.runsMs) || result.runsMs.length === 0) return null;
  return result;
}
