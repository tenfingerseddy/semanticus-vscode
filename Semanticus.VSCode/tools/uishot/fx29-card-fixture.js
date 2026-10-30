// fx29 (rc28 release tester, gaps 4, 5, 6 and 9): states of the measure card added on top of checks-d1-fixture.js. `?fx=` picks one:
// wrap (a number you trust, the tester's place "Total Cost: All" beside two long numbers in a currency format, as Contoso's are), perf (a Performance mark
// with its timings and place values), timing (a measure being timed, the engine's progress words), keep (a drafted Description
// kept, while the engine's read of the Description is slow, as the live read is while it scores the new text).
(function () {
  const fx = new URLSearchParams(location.search).get('fx');
  const engine = window.marksEngine;
  if (!fx || !engine) return;
  const marksOf = name => engine.call('getMeasureMarks', ['measure:Sales/' + name]).marks;
  const markOf = (name, mark) => marksOf(name).find(m => m.name === mark);
  const at = new Date(); at.setHours(6, 44, 0, 0);
  if (fx === 'wrap') {
    Object.assign(markOf('Sales Amount', 'Trusted number'), {
      cells: [{ context: 'Total Cost: All', before: '1000000', after: '89766267.02', beforeText: '$1,000,000.00', afterText: '$89,766,267.02' }] });
  }
  if (fx === 'perf' || fx === 'timing') {
    const timings = [['City', 104, 96, 131, 160], ['Grand total', 100, 92, 118, 210], ['Brand', 98, 90, 125, 150]]
      .map(([context, medianMs, minMs, maxMs, coldMs]) => ({ context, medianMs, minMs, maxMs, coldMs, noiseBandMs: 10, regression: false }));
    Object.assign(markOf('Total Cost', 'Performance'), {
      state: 'passed', sentence: 'Slowest: City, median 104 ms. Within the 3000 ms threshold. 3 contexts timed.', lastRan: at.toISOString(),
      reasonCode: null, applies: true, verdict: 'verified', timings,
      cells: timings.map(t => ({ context: t.context, after: t.medianMs + ' ms' })) });
  }
  if (fx === 'timing') {
    // While a run times the measure, the engine keeps the last result pending and says what the timing does (MarkPerformance).
    Object.assign(markOf('Total Cost', 'Performance'), { pending: true,
      progress: 'Timing this measure: 6 of 21 runs done. Cold: Grand total. About 2 seconds remaining.' });
  }
  if (fx === 'keep') {
    const name = 'ShowValueForDates', ref = 'measure:Sales/' + name;
    const draft = 'Shows the value only for dates that have sales rows, so blank dates stay blank.';
    let description = '', proposal = null;
    const call = engine.call;
    engine.slowRead = 2500;
    engine.call = function (method, args) {
      const n = typeof args[0] === 'string' ? args[0].split('/').slice(1).join('/') : '';
      if (n !== name && !(method === 'keepMeasureDescription' && proposal)) return call.apply(this, arguments);
      switch (method) {
        case 'getMeasureDescription': {
          const view = { ref, name, description, oneLine: description.split('. ')[0], provenance: description ? 'ai' : 'person',
            match: { score: null, level: 'not scored yet', label: 'not scored yet' }, proposal };
          // After Keep the engine's read waits while the new text is scored (tester 06e: about 2 s, then right at 14 s).
          return description && engine.slowRead ? new Promise(r => setTimeout(() => r(view), engine.slowRead)) : view;
        }
        case 'draftMeasureDescription': case 'proposeMeasureDescription':
          proposal = { id: 'fx29-draft', state: 'ready', message: '', text: draft };
          return proposal;
        case 'keepMeasureDescription': description = draft; proposal = null; return { changed: true };
        default: return call.apply(this, arguments);
      }
    };
  }
})();
