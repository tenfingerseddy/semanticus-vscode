// How a query result reads on screen in Queries (Kane, 27 Sep: "friendly headers and formatted dates").
//
// DISPLAY ONLY. The rows themselves are never rewritten: Explain this number, Calculation steps and a saved
// check all read the raw values, so a formatted date can never become a filter the engine does not recognise.
// The header's raw name stays on hover, so the query's own column name is always one pointer away.

const ISO = /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2}))?(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?$/;
const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** "'Store'[Store Region]" or "Store[Store Region]" or "[Net Sales]" or "@Net Sales" becomes "Store region". */
export function friendlyHeader(name) {
  const raw = String(name ?? '').trim();
  const bracket = /\[([^\]]*(?:\]\][^\]]*)*)\]\s*$/.exec(raw);
  // The template names a question's numbers "@Net Sales", and a query run by hand reports that as "[@Net Sales]", so
  // the @ goes whichever way the name arrives (verify-queries-redesign D16: the DAX view read "@Net sales").
  let words = (bracket ? bracket[1].replace(/\]\]/g, ']') : raw.replace(/^'(.*)'$/, '$1')).replace(/^@/, '');
  words = words.replace(/_/g, ' ').replace(/([a-z])([A-Z])/g, '$1 $2').replace(/\s+/g, ' ').trim();
  if (!words) return raw;
  // Sentence case, but an all-capitals word (YTD, SKU, ID) keeps its capitals.
  return words.split(' ').map((w, i) => {
    if (w.length > 1 && w === w.toUpperCase()) return w;
    return i === 0 ? w.charAt(0).toUpperCase() + w.slice(1) : w.charAt(0).toLowerCase() + w.slice(1);
  }).join(' ');
}

/** A plain number (one with no format of its own) as it reads on screen: never rounded to nothing. Two decimals as before for
 *  a figure of 100 or more, and at least four significant digits below that, so 0.0068 reads 0.0068 and 1.0068 reads 1.007
 *  where it used to read 0.01 and 1.01 (a ratio shown as a different number, Sol 3.1). */
export function plainNumber(v) {
  if (!Number.isFinite(v)) return String(v);
  const abs = Math.abs(v);
  if (abs === 0 || abs >= 100) return v.toLocaleString(undefined, { maximumFractionDigits: 2 });
  if (abs >= 1) return v.toLocaleString(undefined, { maximumSignificantDigits: 4, minimumFractionDigits: 0 });
  return v.toLocaleString(undefined, { maximumSignificantDigits: 4 });
}

/** A date string from the engine reads "3 Jan 2025"; a date that carries a time keeps it ("3 Jan 2025 14:05").
 *  Numbers keep up to two decimals with the reader's separators. Everything else is shown as it came. */
export function displayValue(v) {
  if (v === null || v === undefined) return '';
  if (typeof v === 'number') return plainNumber(v);
  if (typeof v === 'boolean') return v ? 'Yes' : 'No';
  const s = String(v);
  const m = ISO.exec(s);
  if (!m) return s;
  const [, y, mo, d, hh, mm] = m;
  const month = MONTHS[Number(mo) - 1];
  if (!month) return s;
  const day = `${Number(d)} ${month} ${y}`;
  return hh && (hh !== '00' || mm !== '00') ? `${day} ${hh}:${mm}` : day;
}

/** A formatter for one column: a year or key column reads "2025", a column of first-of-the-month dates reads
 *  "Jan 2025", any other date column "3 Jan 2025", and everything else goes through displayValue. Decided from the rows, so it follows the data. */
export function columnFormatter(rows, ci, name) {
  // A year, key or code is a label that happens to be a number: 2025, never "2,025".
  if (name && isLabelName(name) && (rows ?? []).every((r) => r?.[ci] == null || Number.isInteger(r[ci]))) {
    return (v) => (v === null || v === undefined ? '' : String(v));
  }
  if (!isDateColumn(rows, ci)) return displayValue;
  let monthly = true;
  for (const r of rows ?? []) {
    const v = r?.[ci];
    if (typeof v !== 'string') continue;
    const m = ISO.exec(v);
    if (!m || m[3] !== '01' || (m[4] && (m[4] !== '00' || m[5] !== '00'))) { monthly = false; break; }
  }
  if (!monthly) return displayValue;
  return (v) => {
    const m = typeof v === 'string' ? ISO.exec(v) : null;
    return m && MONTHS[Number(m[2]) - 1] ? `${MONTHS[Number(m[2]) - 1]} ${m[1]}` : displayValue(v);
  };
}

// Whether a column's NAME says it holds labels that happen to be numbers. The name has to END in the label word, and no
// other word may name an amount: "Year", "Fiscal year", "Order date key" and "Customer ID" are labels, while "Sales Last
// Year", "Prior Year Sales", "Amount Paid" and "Order Number Count" are amounts and keep their separators. The old test
// matched the words anywhere in the name, so those four read 5057989 (verify-queries-redesign D14).
const LABEL_END = /^(year|key|id|code|number|no)$/i;
const AMOUNT_WORD = /^(sales|amount|revenue|cost|costs|price|total|count|sum|margin|profit|qty|quantity|units|value|balance|spend|paid|net|gross|last|prior|previous)$/i;
export function isLabelName(name) {
  const words = friendlyHeader(name).split(' ').filter(Boolean);
  return words.length > 0 && LABEL_END.test(words[words.length - 1]) && !words.slice(0, -1).some((w) => AMOUNT_WORD.test(w));
}

/** True when every non-empty value in a column is a date the engine sent as text. */
export function isDateColumn(rows, ci) {
  let seen = 0;
  for (const r of rows ?? []) {
    const v = r?.[ci];
    if (v === null || v === undefined || v === '') continue;
    if (typeof v !== 'string' || !ISO.test(v)) return false;
    if (++seen >= 50) break;
  }
  return seen > 0;
}
