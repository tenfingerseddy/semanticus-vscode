// Truth derivations and the trivial rule baselines, ported line for line from docs/redesign/jev-pilot/prep.py so the
// comparison is the one docs/redesign/jev-pilot-verdict.md defines. One addition is marked: a type-aware format rule,
// because the pilot never asked the format question of columns and a column's data type is a free, strong signal.
export const NUMERIC = new Set(['int64', 'double', 'decimal']);

export function formatFamily(fs) {
  if (!fs) return 'text_or_none';
  const f = fs.trim(), low = f.toLowerCase();
  if (['short date', 'long date', 'general date', 'short time', 'long time', 'medium date'].includes(low)) return 'date_or_time';
  if (/(yyyy|yy|mmm|dd|hh)/.test(low) && !/#/.test(f)) return 'date_or_time';
  if (f.includes('%')) return 'percent';
  if (/[$£€¥]/.test(f) || low.includes('currency')) return 'currency';
  if (/[#0]\.[#0]/.test(f) || /0\.0/.test(f)) return 'decimal';
  if (/[#0]/.test(f)) return 'whole_number';
  return 'text_or_none';
}

export function summarizeTruth(sb, dtype) {
  const v = (sb || 'default').toLowerCase();
  if (v === 'default') return NUMERIC.has(dtype) ? 'sum' : 'none';
  return v;
}

// pilot baseline_format with an empty DAX body (a column has none)
export function baselineFormat(name) {
  const n = (name || '').toLowerCase();
  if (n.includes('%') || n.includes('percent') || n.includes('pct') || n.endsWith(' rate') || n.includes('margin')
    || n.includes('ratio') || n.includes('growth') || n.includes('share')) return 'percent';
  if (['amount', 'sales', 'cost', 'price', 'revenue', 'profit', 'freight', 'tax', 'margin', 'gross', 'net'].some((k) => n.includes(k))) return 'currency';
  if (n.includes('date')) return 'date_or_time';
  return 'decimal';
}

// ADDED (not in the pilot): data type first, then the pilot's name rule for fractional numbers.
export function baselineFormatTyped(name, dtype) {
  if (dtype === 'dateTime') return 'date_or_time';
  if (dtype === 'int64') {
    const p = baselineFormat(name);
    return p === 'percent' || p === 'currency' ? p : 'whole_number';
  }
  if (dtype === 'string' || dtype === 'boolean') return 'text_or_none';
  return baselineFormat(name);
}

const GEO = [['latitude', 'Latitude'], ['longitude', 'Longitude'], ['postal', 'PostalCode'], ['zip', 'PostalCode'],
  ['address', 'Address'], ['city', 'City'], ['countryregion', 'Country'], ['country', 'Country'],
  ['state', 'StateOrProvince'], ['province', 'StateOrProvince'], ['barcode', 'Barcode'], ['imageurl', 'ImageUrl'],
  ['image', 'ImageUrl'], ['weburl', 'WebUrl'], ['url', 'WebUrl']];

export function baselineDatacat(name) {
  const n = (name || '').toLowerCase().replace(/ /g, '');
  for (const [k, v] of GEO) if (n.includes(k)) return v;
  return 'none';
}

export function baselineHidden(name) {
  const n = (name || '').trim().toLowerCase();
  return n.endsWith('key') || n.endsWith(' id') || n.endsWith('id');
}

export function baselineSummarize(name, dtype) {
  if (!NUMERIC.has(dtype)) return 'none';
  const n = (name || '').trim().toLowerCase();
  if (['key', 'id', 'number', 'code', 'ordinal', 'order', 'year'].some((s) => n.endsWith(s))) return 'none';
  return 'sum';
}

export function baselineSortby(name, siblings) {
  for (const suffix of [' Number', ' Order', ' Sort', ' Index', ' Ordinal', 'Number', 'Sort']) {
    const cand = (name || '') + suffix;
    for (const s of siblings) if (s.toLowerCase() === cand.toLowerCase()) return s;
  }
  return 'none';
}

// Data categories Power BI offers on a column. The pilot fixed eleven and two stored values fell outside them; the
// replay offers the full set the engine would enumerate so no label is unanswerable.
export const DATACAT_CRITERIA = {
  none: 'the values are none of the kinds listed below',
  Country: 'the column holds country or region names',
  StateOrProvince: 'the column holds state, province or region-within-country names',
  County: 'the column holds county names',
  City: 'the column holds city or town names',
  PostalCode: 'the column holds postal codes or zip codes',
  Address: 'the column holds street addresses',
  Place: 'the column holds place names that are not one of the kinds above',
  Continent: 'the column holds continent names',
  Latitude: 'the column holds latitude coordinates',
  Longitude: 'the column holds longitude coordinates',
  ImageUrl: 'the column holds web addresses that point at image files',
  WebUrl: 'the column holds web addresses that point at web pages',
  Barcode: 'the column holds barcode values',
};

export const SUMMARIZE_CRITERIA = {
  sum: 'the values are added together',
  none: 'the values are listed as they are and are not aggregated',
  count: 'the number of rows is counted',
  distinctcount: 'the number of different values is counted',
  average: 'the mean of the values is taken',
  min: 'the smallest value is taken',
  max: 'the largest value is taken',
};

export const FORMAT_CRITERIA = {
  currency: 'the values are displayed with a currency symbol',
  percent: 'the values are displayed as a percentage with a percent sign',
  whole_number: 'the values are displayed as numbers with no decimal places',
  decimal: 'the values are displayed as numbers with one or more decimal places',
  date_or_time: 'the values are displayed as a date or a time',
  text_or_none: 'the values are text rather than numbers',
};

export const HIDDEN_CRITERIA = {
  yes: 'the column does not belong in the field list a report author browses',
  no: 'the column belongs in the field list a report author browses',
};

// ---- The second run's stronger rules (fixed before any Jev answer; README, "Second run"). Each one reads the same
// derived facts the facts arm sees, so Jev is judged against the best a free rule can do with them, as the format
// question was judged against the type-aware rule.

/** Summarize by: none for anything a key, a calendar number or a row id; else the pilot's name rule. */
export function baselineSummarizeTyped(c) {
  if (!NUMERIC.has(c.dataType)) return 'none';
  const m = c.measured ?? {};
  if (c.relationshipRole !== 'none' || c.isKey || m.looksLikeYear || m.looksLikeMonthNumber || m.looksLikeQuarter
    || m.uniqueOnEveryRow) return 'none';
  return baselineSummarize(c.name, c.dataType);
}

/** Data category: a value share of 0.8 or more names the kind; a postal code also needs the name to say so, because
 * plenty of codes are four or five digits. Otherwise the pilot's name rule. */
export function baselineDatacatTyped(c) {
  const m = c.measured ?? {};
  if (c.dataType === 'string') {
    const p = m.placeShare ?? {};
    if (m.imageUrlShare >= 0.8) return 'ImageUrl';
    if (m.urlShare >= 0.8) return 'WebUrl';
    if (p.country >= 0.8) return 'Country';
    if (p.region >= 0.8) return 'StateOrProvince';
    if (p.city >= 0.8) return 'City';
    const byName = baselineDatacat(c.name);
    if (p.postcode >= 0.8 && byName === 'PostalCode') return 'PostalCode';
    return byName === 'PostalCode' ? 'none' : byName;
  }
  const byName = baselineDatacat(c.name);
  return byName === 'Latitude' || byName === 'Longitude' ? byName : 'none';
}

/** Sort by: a month or day name sorts by its first numeric one-to-one partner; otherwise the pilot's name rule,
 * kept only when its pick is a one-to-one partner (the only columns the engine would offer). */
export function baselineSortbyTyped(c, tableColumns) {
  const cands = c.measured?.oneToOneWith ?? [];
  if (c.measured?.looksLikeMonthName || c.measured?.looksLikeDayName) {
    const numeric = cands.filter((n) => NUMERIC.has(tableColumns.find((x) => x.name === n)?.dataType)).sort();
    return numeric[0] ?? 'none';
  }
  const byName = baselineSortby(c.name, tableColumns.filter((x) => x !== c).map((x) => x.name));
  return cands.includes(byName) ? byName : 'none';
}

// ---- The third run's principled rules (README, "Third run"; fixed before any Jev answer). Each is the written rule of
// good practice, applied to ONLY what the product can see: names, types, structure and the derived facts Jev is
// also given. The fair answer key (fairkey.py) applies the same rules with the column's full values and, where the
// data cannot decide, the author's setting. The name patterns are fairkey.py's, ported.

// The product's own "this number is a label" rule (ReadinessRules.cs NonAdditiveDimName), with its camel branch.
const NONADD = /(\b(year|month|quarter|week|day|age|rank|order|sort|sequence|latitude|longitude|lat|lon|lng)$|\b(month|quarter|week|day)\s*(number|num|no)\b|\b(monthnum|monthnumber|weeknum|weeknumber|quarternum|daynum|daynumber)\b|\bpin(?:\s*code)?$|\bpostal\s*code\b|\bpostcode\b|\bzip\s*code\b|\bzip$|\b(?:(?<!\btotal\s)(?<!\bcount\s)(?<!\bunits\s)number|code|id|key)$)/i;
const PRODUCT_CAMEL = /(?<=[a-z])(ID|Id|Key|Code|No|Number|Year)$/;
const CAMEL_ID = /[a-z0-9](ID|Id|Key|SK|Code|No|Num|Number|Year|Month|Quarter|Week|Level)$/;
const ID_OTHER = /(^I[dD]s?(?=[A-Z_\s]|$)|[_\s\-.](id|key|code|no|num)$|^(id|cod|codigo|código|clave|num)\b|\b(level|tier|grade)$)/i;
const ID_OTHER_CASED = /^I[dD]s?(?=[A-Z_\s]|$)/;
const PERIOD_NAME = /(year|yr|month|mth|quarter|qtr|week|wk|day|period|fiscal|semester|half)(?!s)/i;
/** The name as lower-case words, split at spaces, punctuation and camel-case joins (fairkey.py words()). */
export function words(name) {
  const n = String(name ?? '').replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2');
  return n.toLowerCase().split(/[^a-z0-9%\u00c0-\u024f]+/).filter(Boolean);
}
const COUNT_WORDS = new Set(['count', 'counts', 'total', 'totals', 'qty', 'quantity', 'quantities', 'units', 'sum']);
const RATE_WORDS = new Set(['rate', 'rates', 'ratio', 'ratios', 'pct', 'percent', 'percentage', '%', 'share', 'growth', 'avg',
  'average', 'mean', 'score', 'scores', 'rating', 'ratings', 'price', 'prices', 'precio', 'preis', 'prix', 'temperature', 'temp',
  'probability', 'yield', 'efficiency', 'utilization', 'utilisation', 'index', 'height', 'weight', 'bmi', 'speed', 'pressure',
  'humidity', 'concentration', 'satisfaction', 'involvement', 'happy', 'happiness', 'feeling', 'tenure', 'experience', 'years']);
const LABEL_WORDS = new Set(['coded', 'code', 'phone', 'npi', 'offset', 'date']);
const AMOUNT_YEAR = /(last|prior|previous|this|next|current)[\s_]*year$/i;
const countName = (n) => { const w = words(n); return w.some((x) => COUNT_WORDS.has(x)) || w.join(' ').includes('number of'); };
const rateName = (n) => {
  const w = words(n), j = w.join(' ');
  return w.some((x) => RATE_WORDS.has(x)) || String(n ?? '').includes('%') || ['per unit', 'per person', 'per capita', 'unit cost'].some((p) => j.includes(p));
};
const TECH = /(\b(id|key|guid|uuid|sk|fk|pk)$|rowguid|\bhash\b|checksum|\betl\b|^index$|surrogate|row ?version|timestamp$|\b(load|loaded|modified|created|updated|inserted|insert|refresh)(ed)? ?(date|time|dt|on|at|by)$|^_)/i;
const CAMEL_TECH = /[a-z0-9](ID|Id|Key|SK|Guid|GUID)$/;
const ID_LEAD_CASED = /^I[dD]s?(?=[A-Z_\s]|$)/;
const PQ_LEFTOVER = /^((column|columna|colonne|spalte|coluna)\s?\d+|folder path|source\.name|date modified|date accessed|date created|extension|attributes)$/i;
const GLUED_ID = /^[a-z]{3,}id$/;
const NOT_ID = new Set(['paid', 'unpaid', 'prepaid', 'valid', 'invalid', 'solid', 'fluid', 'humid', 'grid', 'acid', 'avid', 'david',
  'rapid', 'timid', 'hybrid', 'liquid', 'squid', 'vivid', 'void', 'android', 'covid', 'orchid', 'pyramid', 'druid', 'cupid', 'lipid',
  'candid', 'splendid', 'madrid', 'said', 'maid', 'raid', 'laid', 'aid', 'rigid']);
const ID_LEAD = /([_\s\-.](id|key|guid|sk|fk|pk)$|^(id|key)[_\s])/i;
const SORT_NAME = /(sort|orden|ordre|\border$|order$|rank$|\bindex$|\bseq$|sequence$|position$|^order\b)/i;

export const techishName = (n) => {
  const s = (n ?? '').trim();
  if (GLUED_ID.test(s) && !NOT_ID.has(s)) return true;
  return TECH.test(s) || CAMEL_TECH.test(s) || ID_LEAD_CASED.test(s) || ID_LEAD.test(s) || PQ_LEFTOVER.test(s);
};
export const labelishName = (n) => {
  const s = (n ?? '').trim();
  if (AMOUNT_YEAR.test(s)) return false;
  const w = words(s);
  if (w.some((x) => LABEL_WORDS.has(x)) || (w.length && w.length <= 2 && ['x', 'y', 'z', 'long'].includes(w[w.length - 1]))) return true;
  if (w.length > 1 && ['min', 'max'].includes(w[0])) return true;
  return NONADD.test(s) || CAMEL_ID.test(s) || ID_OTHER_CASED.test(s) || ID_OTHER.test(s.replace(/^I[dD]s?(?=[A-Z_\s]|$)/, '\u0000'));
};
export const orderingName = (n) => { const s = (n ?? '').trim(); return SORT_NAME.test(s) && !techishName(s) && !/(number|no|num)$/i.test(s); };
/** ColumnFormatApplies's name test (IsNonAdditiveDimensionName), exactly as the product has it. */
export const nonAdditiveDimName = (n) => { const s = (n ?? '').trim(); return !!s && (NONADD.test(s) || PRODUCT_CAMEL.test(s)); };

/** Hidden: keys in relationships, ids and technical columns, and sort helpers are hidden; the rest is visible. */
export function fairRuleHidden(c) {
  const m = c.measured ?? {};
  if (c.relationshipRole === 'many') return true;
  if (c.relationshipRole === 'one') {
    if (c.dataType === 'dateTime') return false;
    if (techishName(c.name)) return true;
    return NUMERIC.has(c.dataType) && !!m.uniqueOnEveryRow && !!m.wholeNumbers;
  }
  if (techishName(c.name)) return true;
  if (NUMERIC.has(c.dataType) && orderingName(c.name)) return true;
  if (c.usedAsSortTarget && orderingName(c.name)) return true;
  return false;
}

/** Summarize by: never sum keys, years, period numbers or row ids; average rates, ratios, prices and measurements;
 * sum additive amounts and quantities. */
export function fairRuleSummarize(c) {
  if (!NUMERIC.has(c.dataType)) return 'none';
  const m = c.measured ?? {};
  if (c.relationshipRole !== 'none' || c.isKey) return 'none';
  if (labelishName(c.name)) return 'none';
  if (m.looksLikeYear) return 'none';
  if (m.looksLikePeriodNumber && PERIOD_NAME.test(c.name)) return 'none';
  if (m.uniqueOnEveryRow && m.wholeNumbers) return 'none';
  if (countName(c.name)) return 'sum';
  if (rateName(c.name)) return 'average';
  return 'sum';
}

/** Needs a sort: the month and day rule (with the pilot's name rule), as the second run's stronger sort rule. */
export function fairRuleNeedsSort(c, tableColumns) {
  return baselineSortbyTyped(c, tableColumns) !== 'none';
}

/** Which partner: the stronger sort rule's pick, else the first numeric one-to-one partner, else the first one. */
export function fairRulePartner(c, tableColumns) {
  const pick = baselineSortbyTyped(c, tableColumns);
  if (pick !== 'none') return pick;
  const cands = [...(c.measured?.oneToOneWith ?? [])].sort();
  const numeric = cands.filter((n) => NUMERIC.has(tableColumns.find((x) => x.name === n)?.dataType));
  return numeric[0] ?? cands[0] ?? 'none';
}

// ---- the product's fallback (ColumnSummarizeFamily.PrincipledRule), for the 26 September re-test -----------------
// The fair rule above, plus the coordinator's guard of 26 September: a yes-or-no flag, a line number or another
// ordinal never gets a total. Ported from ColumnSummarizeFamily.FlagOrOrdinal, word for word.
const FLAG_FIRST = new Set(['is', 'has', 'was', 'were', 'can', 'should', 'did', 'does', 'flag']);
const FLAG_LAST = new Set(['flag', 'flags', 'indicator', 'yn', 'bool', 'boolean']);
const ORDINAL_LAST = new Set(['line', 'seq', 'position', 'pos', 'ordinal', 'step']);
export function flagOrOrdinal(name) {
  const w = words(name);
  if (!w.length) return false;
  return (w.length > 1 && FLAG_FIRST.has(w[0])) || FLAG_LAST.has(w[w.length - 1]) || ORDINAL_LAST.has(w[w.length - 1]);
}
export function productRuleSummarize(c) {
  const r = fairRuleSummarize(c);
  return r !== 'none' && flagOrOrdinal(c.name) ? 'none' : r;   // the C# checks it after every none and before sum or average
}
