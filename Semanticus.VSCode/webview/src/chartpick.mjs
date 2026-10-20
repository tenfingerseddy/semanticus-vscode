// Which chart a Queries result opens as, by the chart rules (Kane, 27 Sep).
//
// SOURCE. Graphicus research, read only: docs/research/03-chart-choice-rules.md section 5 (the decision table,
// first is default) and the hard rules that remove charts which cannot show the shape. Only the kinds Queries
// can draw today are ranked: matrix, table, bar, line, area, card, scatter. Donut, combo, stacked bar and the KPI
// card arrive with the Graphicus work, so a rank that names them falls through to the next kind we have.
//
// WHO PICKS. Free gets this pick. On Pro the brief asks for Jev's pick, labelled with its reason; that needs a Jev
// decision point which does not exist yet, and a new point ships OFF until its replay set clears the bar (beats
// the rules, McNemar p < 0.05, no more harmful picks). Until then Pro gets this same pick, labelled "rules", and
// the label never claims Jev chose. See docs/queries-chart-pick-bar.md.
//
// Jev (when it exists) picks only from `fits`, so a pick can be wrong but never broken.

const KINDS = ['matrix', 'line', 'bar', 'area', 'table', 'card', 'scatter'];

/**
 * A date dim may carry `ordered: false`: the order it is drawn in was not set by the model's sort-by or by what its labels
 * say (daxvisual.tsx axisDims). Such a dim is a category, never time, so no chart claims a time order nobody set (UAT N1).
 * @param {{ dims: { name: string; isDate: boolean; ordered?: boolean; distinct: number }[]; measures: number; rows: number }} shape
 */
export function chartRulePick(shape) {
  const dims = shape?.dims ?? [];
  const m = Math.max(0, shape?.measures ?? 0);
  const rows = Math.max(0, shape?.rows ?? 0);
  const unset = dims.filter((d) => d.isDate && d.ordered === false);
  const dates = dims.filter((d) => d.isDate && d.ordered !== false);
  const cats = dims.filter((d) => !dates.includes(d));
  const misfits = [];
  const out = (rank, reason) => {
    if (unset.length && rows > 0 && m > 0) reason += ` The order of ${unset.map((d) => lower(d.name)).join(' and ')} is the order the data came back in, not a time order.`;
    const fits = KINDS.filter((k) => !misfits.some((x) => x.viz === k));
    const pick = rank.find((k) => fits.includes(k)) ?? 'table';
    return { pick, reason, fits, misfits };
  };

  // No measure, or no grouping the result can be read by: only the rows make sense.
  if (m === 0 || rows === 0) {
    for (const k of KINDS) if (k !== 'table') misfits.push({ viz: k, reason: m === 0 ? 'there is no number to draw' : 'there are no rows to draw' });
    return out(['table'], m === 0 ? 'The result has no number in it, so it shows as rows.' : 'The result has no rows.');
  }
  // Hard rules (section 4 of doc 03): the shape a chart needs.
  // A card is one number: never a split, never two measures, never a column of rows added into a total (Sol 3.6).
  if (dims.length > 0 || rows > 1) misfits.push({ viz: 'card', reason: 'a card shows one number, and this result has many' });
  else if (m > 1) misfits.push({ viz: 'card', reason: 'a card shows one number, and this result has ' + m });
  if (dims.length === 0) {
    for (const k of ['matrix', 'bar', 'line', 'area']) misfits.push({ viz: k, reason: 'there is nothing to split the number by' });
  }
  if (dates.length === 0) {
    for (const k of ['line', 'area']) misfits.push({ viz: k, reason: unset.length ? 'nothing sets the order of these dates, and a line would draw a sequence nobody set' : 'lines are for dates, and there is no date in this result' });
  }
  // A line follows one date, with at most one more split to give each value its own line. Past that it would join unrelated points.
  if (dates.length > 0 && (dims.length > 2 || (dims.length === 2 && m > 1))) {
    for (const k of ['line', 'area']) misfits.push({ viz: k, reason: 'a line follows one date, with one more split at most' });
  }
  if (m < 2) misfits.push({ viz: 'scatter', reason: 'a scatter needs two numbers to plot against each other' });

  if (dims.length === 0) {
    return m === 1 ? out(['card', 'table'], 'One number, so it shows as a card.') : out(['table'], `${m} numbers with no split, so they show side by side as rows.`);
  }
  if (dates.length >= 1 && cats.length === 0) {
    return out(['line', 'bar', 'table'], m === 1 ? 'Dates run left to right, so it shows as a line.' : `Dates run left to right, and ${m} numbers fit as ${m} lines.`);
  }
  if (dates.length >= 1 && cats.length === 1) {
    const k = cats[0].distinct;
    if (k <= 5) return out(['line', 'matrix', 'table'], `Dates run left to right, and ${k} ${plural(k, 'value')} of ${lower(cats[0].name)} fit as ${k} ${plural(k, 'line')}.`);
    return out(['matrix', 'table'], `${k} values of ${lower(cats[0].name)} would crowd one chart, so a grid shows every number.`);
  }
  if (cats.length === 1 && dates.length === 0) {
    const k = cats[0].distinct;
    if (m >= 2) return out(['bar', 'table'], `${m} numbers for each ${lower(cats[0].name)}, so bars sit side by side.`);
    if (k <= 50) return out(['bar', 'table'], `One number for each ${lower(cats[0].name)}, so bars compare them.`);
    return out(['table', 'bar'], `${k} values of ${lower(cats[0].name)} are too many for bars, so they show as rows.`);
  }
  return out(['matrix', 'table'], 'Two ways to split the number, so a grid shows them side by side.');
}

function plural(n, word) { return n === 1 ? word : word + 's'; }
function lower(s) { const t = String(s ?? ''); return t.length > 1 && t === t.toUpperCase() ? t : t.toLowerCase(); }

/** The shape as the engine's pickChartKind reads it: a date whose order nobody set is a category there too, and the
 *  engine's shape has no `ordered` field to carry. */
export function engineShape(shape) {
  return { ...shape, measureNames: Array.isArray(shape?.measureNames) ? shape.measureNames : undefined, dims: (shape?.dims ?? []).map((d) => ({ name: d.name, isDate: d.isDate && d.ordered !== false, distinct: d.distinct })) };
}

/** The chart kinds a switcher shows: the ones this result fits, in the switcher's order. A result with no rules (a query typed
 *  by hand, which no chart is built from) offers the rows only. A button the result cannot use is never shown (K22). */
export function offeredKinds(all, rule) {
  return rule ? all.filter((k) => rule.fits.includes(k)) : all.filter((k) => k === 'table');
}

/** One measure split by two columns is drawn as one series for each value of one column, along the other column, and not as
 *  one run of joined "Brand / Month" labels (a line over those joins the last month of one brand to the first of the next).
 *  Returns which of the two columns is the axis and which gives the series, or null when the joined labels are the better
 *  drawing (two measures, one split, or a bar with too many series to tell apart).
 * @param {{ isDate: boolean; ordered?: boolean; distinct: number }[]} dims
 * @param {number} measures
 * @param {string} viz
 * @param {boolean} [chosen] the person put the two fields in their places themselves (Queries' wells): a bar keeps their order.
 *   Otherwise (an answer in Ask your data) a bar puts the field with MORE values along the axis and the one with FEWER in
 *   the series, so month by year is 12 months with 3 year bars, not 3 years with 12 colours (Kane, 13 Oct). A line always
 *   follows the date.
 * @returns {{ axis: 0 | 1; series: 0 | 1 } | null} */
export function twoSplitAxes(dims, measures, viz, chosen = false) {
  if (measures !== 1 || dims?.length !== 2) return null;
  const time = (d) => d.isDate && d.ordered !== false;
  let axis = time(dims[1]) ? 1 : 0;
  if (!chosen && viz === 'bar' && dims[0].distinct !== dims[1].distinct) axis = dims[1].distinct > dims[0].distinct ? 1 : 0;
  const series = axis === 0 ? 1 : 0;
  if (viz === 'bar' && dims[series].distinct > 12) return null;
  return { axis, series };
}

/** A scatter's points: one measure on each axis, one point for each row. A row missing either number has no point. */
export function scatterPoints(names, cells) {
  const out = [];
  cells.forEach((c, i) => { if (typeof c?.[0] === 'number' && typeof c?.[1] === 'number') out.push({ name: names[i], x: c[0], y: c[1] }); });
  return out;
}
