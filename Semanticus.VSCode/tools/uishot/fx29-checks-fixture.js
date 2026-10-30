// fx29: the d1 fixture (checks-d1-fixture.js) with the rc28 tester's gap states, for fx29-drive.mjs. `&rc28=1` sends what the
// rc28 engine sent (the before shots); without it, what the fixed engine sends. Extra scenarios: advice (Pro advice off for the
// model), added (a measure just added, gap 7); Date New Customer has the format yyyy-mm-dd (gap 8); a run pushes the cheap pass
// for every measure first, as the engine does (gap 2).
// A mock engine for the 1.7 Checks screens after the d1 design pass (checks-d1-drive.mjs). The rc24 tester model: twelve
// Contoso measures on Sales. `?sc=` picks the scenario: sum (after a run), edit (Total Quantity edited), broken (a number you
// trust differs), gap (a number you trust and the measure have different signs), trusted (a number you trust holds, in a currency format), unchecked (two measures never in a run), fresh (nothing run yet; Run fills it in), running (a run in progress).
// Marks carry what the engine's check state sends (L1): a reason code and applies: false on a mark that does not apply,
// pending on a mark a run has not reached. Every state has a version; a change is pushed with a newer one (checks/didChange).
(function () {
  const sc = new URLSearchParams(location.search).get('sc') || 'sum';
  const rc28 = new URLSearchParams(location.search).get('rc28') === '1';
  const CALM = 'Not scored yet. This score turns on when Jev scoring is released.';
  const ADVICE = rc28 ? CALM : 'Not scored yet. Pro advice is off for this model.';
  const at = () => { const d = new Date(); d.setHours(6, 44, 0, 0); return d.toISOString(); };
  // The engine formats every cell once (MarkNumberFormat) and sends the text beside the raw value; the card prints the text.
  const months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  const shown = v => {
    if (v == null) return null;
    const date = /^\d{4}-\d{2}-\d{2}T00:00:00$/.test(v) ? new Date(v + 'Z') : null;
    if (date) return date.getUTCDate() + ' ' + months[date.getUTCMonth()] + ' ' + date.getUTCFullYear();
    return /^-?\d+(\.\d+)?$/.test(v) ? Number(v).toLocaleString('en-US', { maximumFractionDigits: 15 }) : v;
  };
  const M = (name, state, sentence, extra) => {
    const mark = Object.assign({ name, state, sentence, cells: [], action: '', lastRan: null, timings: [], chosenContexts: [] }, extra || {});
    mark.cells = mark.cells.map(c => Object.assign({ beforeText: shown(c.before), afterText: shown(c.after) }, c));
    return mark;
  };
  const places = n => ['City: Aachen', 'City: Aalen', 'City: Berlin', 'Year: 2019', 'Year: 2020', 'Month: March', 'Category: Bikes', 'Grand total']
    .slice(0, n).map((context, i) => ({ id: 'c' + i, column: null, shape: 'Single', role: 'Filter', expectedBehaviour: 'Splits the number by ' + context.split(':')[0] + '.', reason: 'A column the formula reads.' }));
  const off = code => ({ reasonCode: code, applies: false, verdict: null });
  const quiet = () => [
    M('SQL', 'not run', 'No SQL check is saved for this measure.', off('no_sql')),
    M('Trusted number', 'not run', 'No trusted number is saved for this measure.', off('no_trusted')),
    M('Equivalence', 'not run', 'No edit yet.', off('no_edit')),
    M('Alignment', 'not run', 'Not scored yet. This score turns on when Jev scoring is released.', off('not_scored')),
    M('Performance', 'not run', 'not timed', off('not_timed')),
  ];
  const sum = (grand, rows) => [
    M('Totals', 'passed', 'Total adds up as a sum, as expected', { lastRan: at(), total: { rule: 'sum_of_rows', type: 'sum', confidence: .93, fitScore: .9, expected: false, source: '' },
      // The Totals probe reads raw numbers; Filters and Contexts read the model's format. One value per place shows on both.
      cells: rows.map(([context, v]) => ({ context, after: v.replace(/,/g, '') })).concat([{ context: 'Grand total', after: grand.replace(/,/g, '') }]) }),
    M('Filters', 'passed', 'No ignored filter in 157 places checked.', { lastRan: at(), chosenContexts: places(5),
      cells: [{ context: 'Grand total', after: grand }].concat(rows.slice(0, 3).map(([context, after]) => ({ context, after }))) }),
    M('Contexts', 'passed', 'Values in all 163 places checked.', { lastRan: at(), chosenContexts: places(5),
      cells: [{ context: 'Grand total', after: grand }].concat(rows.slice(0, 2).map(([context, after]) => ({ context, after }))) }),
    ...quiet(),
  ];
  const raw = v => /^\d{4}-\d{2}-\d{2}$/.test(v) ? v : new Date(v + ' UTC').toISOString().replace('.000Z', '');
  const dateMeasure = (value, ignores) => [
    // The Totals probe reads the raw date; Filters reads the model's format. The card shows one value per place (gap 4).
    M('Totals', 'passed', 'Total is blank, as expected', { lastRan: at(), total: { rule: 'blank', type: 'blank', confidence: .9, fitScore: .9, expected: false, source: '' },
      cells: [{ context: 'Grand total', after: raw(value) }, { context: 'Year: 2019', after: raw(value) }] }),
    ignores
      ? M('Filters', 'info', 'This formula ignores Date filters. The grand total is ' + value + '. If that is not meant, check the ALLEXCEPT.', { lastRan: at(), chosenContexts: places(4),
        reasonCode: 'filters_lost', actionCodes: ['fix_formula', 'on_purpose'],
        cells: [{ context: 'Grand total', after: value }, { context: 'Year: 2019', after: value }, { context: 'Year: 2020', after: value },
          { context: 'Month: March', after: rc28 ? '28 Sep 2016' : '2016-09-28', afterText: rc28 ? '28 Sep 2016' : '2016-09-28' }] })
      : M('Filters', 'passed', 'No ignored filter in 64 places checked.', { lastRan: at(), chosenContexts: places(4) }),
    M('Contexts', 'passed', 'The sampled contexts have values without errors.', { lastRan: at(), chosenContexts: places(4) }),
    ...quiet(),
  ];
  // A time measure and a yes-or-no flag: no check that applies could run on them (d1 tester gap 2).
  const nothingApplies = filters => [
    M('Totals', 'not run', 'Not scored yet. This score turns on when Jev scoring is released.', off('totals_off')),
    M('Filters', 'not run', filters + ' Add a number you trust to check it.', off('filters_no_rows')),
    M('Contexts', 'not run', filters + ' Add a number you trust to check it.', off('filters_no_rows')),
    ...quiet(),
  ];
  const D = (description, provenance, score, level) => ({ description, provenance,
    match: score == null ? { score: null, level: 'not scored yet', label: 'not scored yet' } : { score, level, label: level[0].toUpperCase() + level.slice(1), reason: 'How the statements compare with the formula.' } });
  const measures = {
    '# Customers': [sum('18,869', [['Category: Bikes', '9,132'], ['Category: Accessories', '15,114'], ['Category: Clothing', '6,852']]), D('Counts the customers who bought something.', 'person', 91, 'matches')],
    '# Orders': [sum('27,659', [['Year: 2019', '9,048'], ['Year: 2020', '18,611']]), D('Counts the sales orders.', 'person', 93, 'matches')],
    '# Sales': [sum('60,398', [['Year: 2019', '19,412'], ['Year: 2020', '40,986']]), D('Counts the sales rows.', 'ai', 88, 'matches')],
    'Date Lost Customer': [dateMeasure('28 Sep 2020', false), D('The last date a customer bought something.', 'person', 81, 'matches')],
    'Date New Customer': [dateMeasure(rc28 ? '12 Sep 2016' : '2016-09-12', true), D('The date of a customer’s first order.', 'person', 64, 'could be improved')],
    'Gross Profit': [sum('12,080,883', [['Year: 2019', '4,021,553'], ['Year: 2020', '8,059,330']]), D('Sales Amount minus Total Cost.', 'person', 95, 'matches')],
    'Quantity PY': [nothingApplies('This formula moves the date window, so the row count test does not apply.'), D('Units sold in the same period last year.', 'ai', 77, 'matches')],
    'Sales Amount': [sum('29,358,677', [['Year: 2019', '9,791,060'], ['Year: 2020', '19,567,617']]), D('Sales after discounts, before tax.', 'person', 92, 'matches')],
    'Sales PY': [sum('16,351,550', [['Year: 2020', '16,351,550']]).map(m => m.name === 'Filters' ? M('Filters', 'not run', 'This formula moves the date window, so the row count test does not apply. Add a number you trust to check it.', off('filters_no_rows')) : m), D('Sales Amount in the same period last year.', 'ai', 84, 'matches')],
    'ShowValueForDates': [nothingApplies('This measure returns a yes-or-no result, so a row count cannot check it.'), D('', 'person', null)],
    'Total Cost': [sum('17,277,794', [['Year: 2019', '5,769,507'], ['Year: 2020', '11,508,287']]), D('Quantity times unit cost, for each sales row.', 'person', 90, 'matches')],
    'Total Quantity': [sum('628,370', [['City: Aachen', '61'], ['City: Aalen', '16'], ['City: Aalen Waldhausen', '39']]),
      D('Total units sold. Adds up Sales[Quantity] over every sales row in the current filters, so it follows every slicer.', 'ai', 94, 'matches')],
  };
  const formulas = { 'Total Quantity': 'SUM ( Sales[Quantity] )' };
  if (sc === 'edit') {
    const tq = measures['Total Quantity'];
    tq[0] = tq[0].map(m => m.name !== 'Equivalence' ? m : M('Equivalence', 'suspect', 'This change moved these numbers.', { lastRan: new Date().toISOString(),
      beforeFormula: 'SUM ( Sales[Quantity] )', afterFormula: 'SUM ( Sales[Quantity] ) * 2', beforeFormatString: '#,0', afterFormatString: '#,0', inputs: 'edit-1',
      cells: [['Grand total', 628370], ['City: Aachen', 61], ['City: Aalen', 16], ['City: Aalen Waldhausen', 39], ['City: Wasseralfingen', 10], ['City: Unterkochen', 1]]
        .map(([context, v]) => ({ context, before: String(v), after: String(v * 2) })) }));
    tq[1] = D(tq[1].description, 'ai', 58, 'could be improved');
    formulas['Total Quantity'] = 'SUM ( Sales[Quantity] ) * 2';
  }
  if (sc === 'broken') {
    const sa = measures['Sales Amount'];
    sa[0] = sa[0].map(m => m.name !== 'Trusted number' ? m : M('Trusted number', 'suspect', 'The measure differs from a number you trust.', { lastRan: at(),
      cells: [{ context: 'Grand total', before: '29,358,677', after: '29,358,665' }] }));
  }
  if (sc === 'gap') {
    // fx28: the two numbers have different signs; the engine writes the gap in the measure's format (MarkNumberFormat.Gap).
    const sa = measures['Sales Amount'];
    sa[0] = sa[0].map(m => m.name !== 'Trusted number' ? m : M('Trusted number', 'suspect', 'The measure differs from a number you trust.', { lastRan: at(),
      cells: [{ context: 'Grand total', before: '1000', after: '-5', beforeText: '$1,000.00', afterText: '-$5.00', gapText: '$1,005.00' }] }));
  }
  if (sc === 'trusted') {
    // The engine sends the raw value and its text in the measure's format; the sentence must read the text (v-rc28 polish).
    const sa = measures['Sales Amount'];
    sa[0] = sa[0].map(m => m.name !== 'Trusted number' ? m : M('Trusted number', 'passed', 'The measure matches the saved numbers you trust.', { lastRan: at(),
      cells: [{ context: 'Grand total', before: '628370', after: '628370', beforeText: '$628,370.00', afterText: '$628,370.00' }] }));
  }
  if (sc === 'advice') for (const n of Object.keys(measures))
    measures[n][0] = measures[n][0].map(m => m.name === 'Totals' ? M('Totals', 'not run', ADVICE, off('totals_off')) : m);
  // Gap 7: New Measure on Sales, as the engine reads it right after the add, before its checks run. The rc28 engine compared it
  // as a rename it could not trace; the fixed engine has no edit of it, so its marks wait.
  if (sc === 'added') measures['New Measure'] = [[
    M('Totals', 'not run', CALM, off('totals_off')),
    M('Filters', 'not run', 'This check is waiting to run.', { reasonCode: 'waiting', applies: true, pending: true, verdict: null }),
    M('Contexts', 'not run', 'This check is waiting to run.', { reasonCode: 'waiting', applies: true, pending: true, verdict: null }),
    M('SQL', 'not run', 'No SQL check is saved for this measure.', off('no_sql')),
    M('Trusted number', 'not run', 'No trusted number is saved for this measure.', off('no_trusted')),
    rc28 ? M('Equivalence', 'not run', 'The old measure identity could not be recovered after this rename.', { reasonCode: 'could_not_run', applies: true, verdict: 'unknown' })
      : M('Equivalence', 'not run', 'No edit yet.', off('no_edit')),
    M('Alignment', 'not run', CALM, off('not_scored')),
    M('Performance', 'not run', 'not timed', off('not_timed')),
  ], D('', 'person', null)];
  const names = Object.keys(measures);
  const ref = n => 'measure:Sales/' + n;
  const never = new Set(sc === 'unchecked' ? ['Quantity PY', 'ShowValueForDates'] : []);
  const engine = { calls: [], ran: sc !== 'fresh' && sc !== 'running', done: 0, release: null, kept: {}, version: 10 };
  const marksOf = n => engine.ran && !never.has(n) ? measures[n][0] : [];
  const checkedOf = n => engine.ran && !never.has(n) && n !== 'New Measure' ? at() : null;
  // Keep is the engine's (keepMeasureEdit): the kept Equivalence no longer applies while its inputs are the kept ones.
  const finished = (n, marks) => marks.map(m => m.name === 'Equivalence' && engine.kept[ref(n)] && engine.kept[ref(n)] === m.inputs ? Object.assign({}, m, off('kept')) : m);
  const versions = {};
  const stateOf = n => ({ measureRef: ref(n), version: versions[n] ?? engine.version, running: false, lastChecked: checkedOf(n), marks: finished(n, marksOf(n)) });
  // checks/didChange: the extension posts { type: 'checkState' } to the page; the drive calls this after it changes a state.
  engine.push = list => { const v = ++engine.version; for (const n of list) versions[n] = v;
    window.dispatchEvent(new MessageEvent('message', { data: { type: 'checkState', payload: { version: v, states: list.map(stateOf) } } })); };
  const plan = req => {
    const sel = req && req.selection;
    let picks;
    if (sel && sel.measures && sel.measures.length) picks = sel.measures.map(r => ({ measureRef: r, group: 'selected', reason: 'Ticked.' }));
    else if (never.size) picks = [...never].map(n => ({ measureRef: ref(n), group: 'oldest', reason: 'Never checked.' }));
    else picks = names.map((n, i) => ({ measureRef: ref(n), group: i < 4 ? 'core' : i < 9 ? 'risky' : 'oldest',
      reason: i < 4 ? 'Used by 2 other measures.' : i < 9 ? 'Formula uses filter changes.' : 'Never checked.' }));
    return { id: 'plan-' + picks.length + (req && req.includePerformance ? '+perf' : ''), totalMeasures: names.length, batchSize: 40,
      estimatedSeconds: picks.length * 15, sentence: '', picks, includePerformance: !!(req && req.includePerformance) };
  };
  engine.call = function (method, args) {
    engine.calls.push({ method, args });
    const r = args[0], n = typeof r === 'string' ? r.split('/').slice(1).join('/') : '';
    switch (method) {
      case 'getChecksPage': return { sessionId: 'Contoso', revision: 1,
        measures: names.map(x => ({ ref: ref(x), name: x, table: 'Sales', folder: '', description: measures[x][1].description,
          oneLine: measures[x][1].description.split('. ')[0], descriptionProvenance: measures[x][1].provenance, descriptionMatch: engine.ran ? measures[x][1].match : null })),
        summary: { total: names.length, numbersDiffer: sc === 'broken' ? 1 : 0, keyProblems: 0, tableProblems: 0 },
        numbers: { definitions: [{ id: 'sales-all', title: 'Sales Amount, all rows', enabled: true, kind: 'measureValue' }] },
        results: engine.ran ? { 'sales-all': sc === 'broken' ? { verdict: 'Fail', expected: '29,358,677', actual: '29,358,665', difference: -12 } : { verdict: 'Pass', expected: '29,358,677', actual: '29,358,677' } } : {},
        tables: { rows: engine.ran ? [{ table: 'Sales', lastModelCount: 60398 }, { table: 'Customer', lastModelCount: 18484 }] : [] },
        relationships: engine.ran ? { relationships: [{ name: 'Sales to Customer', dataTypeMatch: { verdict: 'Pass' }, keyUniqueness: { verdict: 'Pass' }, referentialIntegrity: { verdict: 'Pass' } }] } : null };
      case 'getMeasureCheckCache':
        if (!engine.ran) return names.slice(0, engine.done).map(x => ({ measureRef: ref(x), version: engine.version, lastChecked: new Date().toISOString(), marks: measures[x][0] }));
        return names.map(stateOf);
      case 'keepMeasureEdit': { const eq = marksOf(n).find(m => m.name === 'Equivalence'); engine.kept[r] = eq && eq.inputs; engine.push([n]); return stateOf(n); }
      case 'stopMeasureRun': if (engine.release) { const done = engine.done; engine.stopped = true; engine.release(); engine.done = done; } return true;
      case 'getMeasureMarks':
        // Check now (refresh) holds the measure in its run until the driver calls finishRecheck. The engine's start view:
        // every mark waits, SQL and Trusted number read "No ... is saved", LastRan comes from the previous run (fx24 D4).
        if (args[2] === true && engine.ran) engine.rechecking = n;
        // The engine's state for the measure: a mark the run has not reached keeps its last result with pending set (fx24 D4).
        if (engine.rechecking === n) return { measureRef: r, revision: 7, version: ++engine.version, running: true, lastChecked: checkedOf(n), performanceSettings: { slowThresholdMs: 3000 },
          marks: finished(n, marksOf(n)).map(m => m.applies === false ? m : Object.assign({}, m, { pending: true })) };
        return { measureRef: r, revision: 7, version: versions[n] ?? engine.version, running: sc === 'running' && !engine.ran, lastChecked: checkedOf(n), marks: finished(n, marksOf(n)),
        performanceSettings: { slowThresholdMs: 3000 } };
      case 'getMeasureDescription': { const d = measures[n] ? measures[n][1] : D('', 'person', null);
        return { ref: r, name: n, description: d.description, oneLine: d.description.split('. ')[0], provenance: d.provenance, match: engine.ran ? d.match : null, proposal: null }; }
      case 'planMeasureRun': return plan(r);
      case 'runMeasurePlan': return new Promise(resolve => { engine.release = () => { engine.release = null;
        if (!engine.stopped) engine.ran = true;
        resolve({ planId: r, stoppedOnEdit: false, stopped: !!engine.stopped, results: engine.ran ? names.map(stateOf) : names.slice(0, engine.done).map(x => ({ measureRef: ref(x), version: ++engine.version, lastChecked: new Date().toISOString(), marks: measures[x][0] })) }); };
        if (sc === 'fresh') setTimeout(engine.release, 50); });
      case 'listSqlSources': return [];
      case 'cleanupMeaningAnnotations': return { note: 'Removed 6 old meaning annotations from 3 measures. Undo restores them.' };
      default: return {};
    }
  };
  // The run line counts the measures the engine pushed as checked since the run began.
  // The engine's cheap pass (Parse, Lint, Orphan keys, Description match) writes every measure first, each with a time, then
  // the run checks them one by one: a measure not reached yet has only the cheap marks (rc28 tester gap 2).
  const cheapMarks = () => [M('Parse', 'passed', 'The formula parses and its references resolve.', { lastRan: new Date().toISOString() }),
    M('Lint', 'passed', 'No lint findings.', { lastRan: new Date().toISOString() }),
    M('Orphan keys', 'not run', 'No relationship counts apply to this measure.'),
    M('Description match', 'not run', CALM, { reasonCode: 'not_scored' })];
  engine.pushDone = count => { engine.done = count; const v = ++engine.version;
    window.dispatchEvent(new MessageEvent('message', { data: { type: 'checkState', payload: { version: v,
      states: names.map((x, i) => i < count ? { measureRef: ref(x), version: v, running: false, lastChecked: new Date().toISOString(), marks: measures[x][0] }
        : { measureRef: ref(x), version: v, running: false, lastChecked: null, marks: [], cheap: cheapMarks() }) } } })); };
  // v-fx24 N1: a measure Total Quantity uses changes (on) or Undo takes it back (off). The compare run keeps the measure's own
  // formula on both sides, moves the numbers on new inputs, and Undo restores the kept mark.
  if (sc === 'edit') {
    const tq = measures['Total Quantity'][0], kept = tq.findIndex(m => m.name === 'Equivalence'), mark = tq[kept];
    const moved = M('Equivalence', 'suspect', 'This change moved these numbers.', { lastRan: new Date(Date.now() + 60000).toISOString(),
      beforeFormula: mark.afterFormula, afterFormula: mark.afterFormula, beforeFormatString: '#,0', afterFormatString: '#,0', inputs: 'edit-2',
      cells: mark.cells.map(c => ({ context: c.context, before: c.after, after: String(Number(c.after) + 5) })) });
    engine.editUsed = on => { tq[kept] = on ? moved : mark; engine.push(['Total Quantity']); };
  }
  engine.finishRecheck = () => { engine.rechecking = null; };
  engine.formulas = formulas;
  window.marksEngine = engine;
})();
