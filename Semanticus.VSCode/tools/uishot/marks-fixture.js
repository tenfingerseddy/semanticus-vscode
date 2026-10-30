// A mock engine for the marks screens (Properties, mark cards, run plan, Checks page). Contoso names, made-up numbers.
// marks-drive.mjs loads it into every page before the page's own scripts run.
(function () {
  const M = (name, state, sentence, extra) => Object.assign({ name, state, sentence, cells: [], action: '', lastRan: null, timings: [], chosenContexts: [] }, extra || {});
  const night = () => { const d = new Date(); d.setHours(2, 10, 0, 0); return d.toISOString(); };
  const ctx = (id, table, name, expected, reason, shape) => ({ id, column: table ? { table, name } : null, shape: shape || 'Single', role: 'Filter', expectedBehaviour: expected, reason });
  const base = () => [
    M('Totals', 'not run', 'Not scored yet. This score is not available yet.'),
    M('Filters', 'passed', 'Each filter tested acts as the formula says.', { lastRan: night() }),
    M('SQL', 'not run', 'No SQL check is saved for this measure.'),
    M('Trusted number', 'not run', 'No trusted number is saved for this measure.'),
    M('Equivalence', 'not run', 'No edit yet.'),
    M('Contexts', 'passed', 'The sampled contexts have values without errors.', { lastRan: night(),
      cells: [{ context: 'grand total', after: '3,214,500' }, { context: 'single-select: Channel=Store', after: '1,402,113' }] }),
    M('Alignment', 'not run', 'Not scored yet. This score is not available yet.'),
    M('Performance', 'not run', 'not timed'),
  ];
  const set = (marks, name, patch) => marks.map(m => m.name === name ? Object.assign({}, m, patch) : m);
  const measures = {
    'measure:Sales/Sales Tailspin Toys': { table: 'Sales', folder: 'Sales\\Brands', provenance: 'person',
      description: 'Sales amount for Tailspin Toys products only. It keeps Tailspin Toys on every row, whatever manufacturer you filter by.',
      match: { score: 93, level: 'matches', label: 'Matches 93%', reason: 'The formula keeps Tailspin Toys, as the Description says.' },
      marks: set(base(), 'Filters', { state: 'suspect', sentence: 'The number stays the same when you filter by Manufacturer.', lastRan: night(),
        cells: [{ context: 'Manufacturer: Contoso, Ltd', after: '1,284,310' }, { context: 'Manufacturer: Fabrikam, Inc.', after: '1,284,310' },
          { context: 'Manufacturer: Litware, Inc.', after: '1,284,310' }, { context: 'Grand total', after: '1,284,310' }],
        chosenContexts: [ctx('c1', 'Product', 'Manufacturer', 'Shows Tailspin Toys on every row, because the formula overwrites your filter.', 'The formula filters Manufacturer.'),
          ctx('c2', 'Product', 'Category', 'Splits the Tailspin Toys figure.', 'A sibling of the filtered column.'),
          ctx('c3', 'Store', 'Store Name', 'Should simply split the number.', 'An unrelated filter.'),
          ctx('c4', null, null, 'The figure for all Tailspin Toys sales.', 'The total row.', 'GrandTotal')] }) },
    'measure:Sales/Total Quantity': { table: 'Sales', folder: '', provenance: 'ai', description: 'Number of units sold.',
      match: { score: 68, level: 'could be improved', label: 'Could be improved', reason: 'Missing detail. It does not say what a total row shows, or that returns are already taken out.' },
      marks: base() },
    'measure:Sales/ShowValueForDates': { table: 'Sales', folder: '', provenance: 'person', description: '',
      match: { score: null, level: 'not scored yet', reason: 'This score turns on when Jev scoring is released.' },
      marks: base().map(m => ({ ...m, state: 'not run', sentence: 'Not run yet.' })) },
    'measure:Sales/Sales YTD': { table: 'Sales', folder: 'Sales\\Time', provenance: 'ai', description: 'Sales Amount from 1 January to the selected date, in the same calendar year.',
      match: { score: 38, level: 'does not match', label: 'Does not match', reason: 'A contradiction. The Description says calendar year. The formula now ends the year on 30 June.' },
      marks: set(set(base(), 'Equivalence', { state: 'suspect', sentence: 'This change moved these numbers.', lastRan: new Date().toISOString(),
        cells: [{ context: 'Mar 2025', before: '9,412,880', after: '4,118,020' }, { context: 'Dec 2025', before: '31,560,400', after: '16,204,770' },
          { context: 'Year 2025', before: '31,560,400', after: '16,204,770' }] }),
        'Trusted number', { state: 'suspect', sentence: 'The measure differs from a number you trust.', lastRan: new Date().toISOString(),
          cells: [{ context: 'Year 2025', before: '31,560,400', after: '16,204,770' }] }) },
    'measure:Sales/Total Cost': { table: 'Sales', folder: '', provenance: 'person', description: 'Cost of the goods sold: quantity times unit cost, for each line.',
      match: { score: 90, level: 'matches', label: 'Matches 90%' },
      marks: set(base(), 'Equivalence', { state: 'passed', sentence: 'Nothing moved on the sampled grid.', lastRan: new Date().toISOString() }) },
    'measure:Sales/Gross Margin %': { table: 'Sales', folder: 'Sales\\Margins', provenance: 'person',
      description: 'Sales Amount minus Total Cost, divided by Sales Amount. A total row works the rate out again from the totals. It does not add up the rows.',
      match: { score: 41, level: 'does not match', label: 'Does not match', reason: 'A contradiction. The Description says a total row works the rate out again. The formula adds up the channel rates.' },
      marks: set(base(), 'Contexts', { state: 'passed', sentence: 'The sampled contexts have values without errors.', lastRan: night(),
        cells: [{ context: 'Channel: Store', after: '12%' }, { context: 'Channel: Online', after: '15%' }, { context: 'Channel: Catalog', after: '9%' }, { context: 'Grand total', after: '36%' }] }) },
    'measure:Inventory/Inventory Balance': { table: 'Inventory', folder: 'Inventory', provenance: 'person',
      description: 'Takes the stock count on the last date that has a count in the period, and adds it across stores and products. It does not add up over time.',
      match: { score: 95, level: 'matches', label: 'Matches 95%' },
      marks: set(set(base(), 'Contexts', { state: 'suspect', sentence: 'It is blank in some places only.', lastRan: night(),
        cells: [{ context: 'Mar 2026: Store 12', after: 'BLANK' }, { context: 'Mar 2026: Store 31', after: 'BLANK' }, { context: 'Mar 2026: Store 40', after: 'BLANK' }, { context: 'Mar 2026: 64 other stores', after: '1,040 to 1,310' }],
        chosenContexts: [ctx('i1', 'Date', 'Month', 'Should show the last balance of the period.', 'The formula takes the last date.'),
          ctx('i2', 'Store', 'Store Name', 'Should simply split the balance.', 'A store has its own count.')] }),
        'Performance', { state: 'passed', sentence: 'Median 180 ms, within your limit of 3 s.', lastRan: night(),
          timings: [{ context: 'Grand total', medianMs: 180, minMs: 160, maxMs: 230, coldMs: 640 }, { context: 'Month', medianMs: 410, minMs: 380, maxMs: 520, coldMs: 900 }] }) },
  };
  measures['measure:Sales/Customers'] = { table: 'Sales', folder: 'Customers', description: 'Counts distinct Contoso customers.',
    provenance: 'person', match: { score: 92, level: 'matches', label: 'Matches 92%' }, marks: base() };
  measures['measure:Sales/Gross Profit'] = { table: 'Sales', folder: '', description: 'Contoso sales minus cost.', provenance: 'person', marks: [] };
  measures['measure:Sales/Unit Price'] = { table: 'Sales', folder: '', description: 'Contoso sales per unit.', provenance: 'person',
    marks: base().map(m => Object.assign({}, m, { state: 'passed', sentence: 'The checked value matches.', lastRan: night() })) };
  const totalTypes = {
    'Total Quantity': ['sum_of_rows', 'sum', 'Total adds up as a sum, as expected', [10, 20, 30], 60, 99],
    'Gross Margin %': ['recomputed_at_total', 'recalc (recomputed at the total)', 'Total is recomputed at the total, as expected', [.1, .2, .3], .22, .6],
    'Inventory Balance': ['last_value_over_time', 'semi-additive (last value over time)', 'Total shows the last value over time, as expected', [1100, 1200, 1000], 1000, 3300],
    'Customers': ['distinct_count', 'distinct count', '', [40, 55, 30], 90, 125],
    average: ['average', 'average', 'Total is an average, as expected', [10, 20, 30], 20, 60],
    blank: ['blank_at_total', 'blank at total', 'Total is blank, as expected', [10, 20, 30], 'BLANK', 60],
    first: ['first_value_over_time', 'semi-additive (first value over time)', 'Total shows the first value over time, as expected', [1100, 1200, 1000], 1100, 3300],
  };
  const totalsMark = ref => {
    const options = new URLSearchParams(location.search), state = options.get('totals') || 'passed';
    const name = ref.split('/').slice(1).join('/');
    const data = totalTypes[options.get('totalrule') || name];
    if (!data) return null;
    if (state === 'unavailable') return M('Totals', 'not run', 'Not scored yet. The scoring service does not have this check yet.');
    const [rule, type, passed, rows, right, wrong] = data;
    const total = state === 'warning' ? wrong : right;
    const article = rule === 'average' ? 'a row average' : rule === 'blank_at_total' ? 'a blank total' : 'a ' + type;
    let sentence = passed, markState = 'passed';
    if (state === 'warning') { markState = 'suspect'; sentence = `The total does not fit ${article}: rows ${rows.join(', ')}; total ${total}`; }
    if (state === 'info' || rule === 'distinct_count') { markState = 'info'; sentence = `Checked as ${article}. Totals for this type are shown, not judged`; }
    if (state === 'unclear') { markState = 'info'; sentence = 'Type unclear'; }
    if (state === 'waived') { markState = 'info'; sentence = `Checked as ${article}. This is expected`; }
    return M('Totals', markState, sentence, { lastRan: night(), action: markState === 'suspect' ? 'This is expected' : null,
      total: { rule, type, confidence: state === 'unclear' ? .45 : state === 'info' ? .7 : .94, expected: state === 'waived',
        fitScore: state === 'warning' ? .1 : .85, source: 'The type came from Jev reading the name, the Description and the formula.' },
      cells: rows.map((v, i) => ({ context: ['Bikes', 'Accessories', 'Clothing'][i], after: String(v) })).concat([{ context: 'Grand total', after: String(total) }]),
      chosenContexts: [ctx('total', null, null, 'Recalculate at the grand total.', 'Compare complete grouped rows.', 'GrandTotal')],
    });
  };
  for (const [ref, measure] of Object.entries(measures)) { const total = totalsMark(ref); if (total) measure.marks = set(measure.marks, 'Totals', total); }
  const descriptionScore = new URLSearchParams(location.search).get('descscore');
  if (descriptionScore !== null) {
    const score = descriptionScore === 'unavailable' ? null : Number(descriptionScore);
    const label = score === null ? 'not scored yet' : score >= 70 ? 'Matches' : score >= 50 ? 'Could be improved' : 'Does not match';
    measures['measure:Sales/Total Quantity'].description = 'Adds the Contoso quantities sold after returns.';
    measures['measure:Sales/Total Quantity'].match = { score, label, level: label.toLowerCase(),
      reason: score === null ? 'The scoring service does not have this check yet.' : score >= 70 ? 'The Description statements match the formula.'
        : 'Some Description statements may differ from the formula. Compare the calculation, filters and dates.' };
  }
  const order = Object.keys(measures);
  if (new URLSearchParams(location.search).get('fx17ratio') === '1') {
    const m = measures['measure:Sales/Total Quantity'];
    m.marks = set(m.marks, 'Equivalence', { state: 'suspect', sentence: 'This change moved these numbers.',
      beforeFormatString: '#,0', afterFormatString: '0.####%', cells: [
        { context: 'Grand total', before: '628370', after: '1' },
        { context: 'City: Contoso', before: '61', after: '9.707656317137992E-05' } ] });
  }
  if (new URLSearchParams(location.search).get('unreleased') === '1') {
    const m = measures['measure:Sales/Total Quantity'];
    m.marks = set(m.marks, 'Totals', { state: 'not run', sentence: 'Not scored yet. The scoring service is unavailable right now.', total: null, cells: [], chosenContexts: [] });
    m.match = { score: null, level: 'not scored yet', reason: 'The scoring service is unavailable right now.' };
  }
  const names = r => r.split('/').slice(1).join('/');
  let lastRun = null;
  const sources = () => (new URLSearchParams(location.search).get('sql') === '1' ? [{ id: 's1', name: 'ContosoDW' }] : []);
  const plan = (req) => {
    req = req || {};
    const sel = req.selection;
    let picks;
    if (!sel) {
      picks = [];
      const groups = [['changed', 6, 'Changed since the last run, or uses a measure that changed.'], ['core', 10, 'Used by 4 other measures.'], ['risky', 16, 'Formula uses last values over dates.'], ['oldest', 2, 'Oldest check: 2026-10-27 01:02:04Z.']];
      let i = 0;
      for (const [group, n, reason] of groups) for (let k = 0; k < n; k++) {
        const ref = order[i] || ('measure:Sales/Contoso measure ' + (i + 1)); i++;
        picks.push({ measureRef: ref, group, reason });
      }
    } else if (sel.kind === 'changed') picks = ['measure:Sales/Sales YTD', 'measure:Sales/Total Cost'].map(r => ({ measureRef: r, group: 'changed', reason: 'Changed since the last run.' }));
    else picks = (sel.measures || []).map(r => ({ measureRef: r, group: 'selected', reason: 'Selected by the person or their agent.' }));
    return { id: 'plan-' + picks.map(p => p.measureRef).join('|') + (req.includePerformance ? '+perf' : ''), totalMeasures: 500, batchSize: 40,
      estimatedSeconds: sel ? picks.length * 5 : 110, sentence: '', picks, includePerformance: !!req.includePerformance };
  };
  const cache = () => order.map(r => ({ measureRef: r, lastChecked: night(), marks: measures[r].marks }));
  const engine = {
    calls: [],
    call(method, args) {
      engine.calls.push({ method, args });
      const ref = args[0];
      switch (method) {
        case 'getMeasureMarks': {
          const m = measures[ref];
          return { measureRef: ref, revision: 1, running: false, marks: m ? m.marks : base() };
        }
        case 'getMeasureDescription': {
          const m = measures[ref] || {};
          return { ref, name: names(ref), description: m.description || '', oneLine: (m.description || '').split('. ')[0] + ((m.description || '').includes('. ') ? '.' : ''),
            provenance: m.provenance || 'not recorded', match: m.match || { score: null, level: 'not scored yet', label: 'not scored yet' }, proposal: m.proposal || null };
        }
        case 'setDescription': measures[ref].description = args[1]; measures[ref].provenance = 'person'; return { changed: true };
        case 'draftMeasureDescription': case 'proposeMeasureDescription':
          if (new URLSearchParams(location.search).get('descwait') === '1')
            return measures[ref].proposal = { id: 'proposal-1', state: 'waiting', message: 'Waiting for your assistant to draft a Description. The wait ends after 60 seconds.' };
          return measures[ref].proposal = { id: 'proposal-1', state: 'ready', message: 'Your assistant proposed a Description. Keep it or discard it.',
            text: 'Number of units sold, after returns are taken out. A total row adds up the units.' };
        case 'keepMeasureDescription': { const m = order.map(r => measures[r]).find(x => x.proposal); if (m) { m.description = m.proposal.text; m.provenance = 'ai'; m.proposal = null; } return { changed: true }; }
        case 'planMeasureRun': return plan(args[0]);
        case 'runMeasurePlan': lastRun = args[0]; return { planId: args[0], stoppedOnEdit: false,
          results: args[0].replace(/^plan-/, '').replace(/\+perf$/, '').split('|').map(r => ({ measureRef: r, lastChecked: new Date().toISOString(), marks: (measures[r] || {}).marks || base() })) };
        case 'getMeasureCheckCache': return cache();
        case 'setMeasurePerformance': return {};
        case 'setMeasureTotals': {
          const measure = measures[args[1]];
          measure.marks = set(measure.marks, 'Totals', { state: 'info', sentence: 'Checked as a ' + measure.marks.find(m => m.name === 'Totals').total.type + '. This is expected', action: null,
            total: Object.assign({}, measure.marks.find(m => m.name === 'Totals').total, { expected: true }) });
          return {};
        }
        case 'listSqlSources': return sources();
        case 'saveTest': return Object.assign({ id: 'trusted-1' }, args[0]);
        case 'getChecksPage': return { sessionId: 'Contoso', revision: 1,
          measures: order.map(r => ({ ref: r, name: names(r), description: measures[r].description, oneLine: measures[r].description.split('. ')[0], descriptionProvenance: measures[r].provenance,
            descriptionMatch: measures[r].match, table: measures[r].table, folder: measures[r].folder })),
          summary: { total: order.length, numbersDiffer: 0, keyProblems: 0, tableProblems: 0 }, numbers: { definitions: [] }, results: {}, tables: { rows: [] }, relationships: null };
        case 'cleanupMeaningAnnotations': return { measures: 3, annotations: 6, note: 'Removed 6 old meaning annotations from 3 measures. Undo restores them. Description stays the same.' };
        default: return {};
      }
    },
    get lastRun() { return lastRun; },
    measures,
  };
  window.marksEngine = engine;
})();
