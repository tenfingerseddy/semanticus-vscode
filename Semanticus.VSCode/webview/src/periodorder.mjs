// The order a column of time labels reads in, for the axis of a chart and the columns of a matrix (UAT N1: a text
// "YearMonth" axis ran 2024-03, 2025-08, 2024-09, 2024-02 under a caption that said "Dates run left to right").
//
// This is the order of LAST resort: a field the model gives a sort-by column is ordered by that column (daxwells.tsx
// asks for it, daxvisual.tsx folds it back), and only a field with none is put in order by what its labels SAY. A month
// name sorts January to December and a period like 2024-03 sorts by year then month. When any label in the column is not
// one this reads, it returns null and the caller keeps the engine's order and claims nothing, never an invented one.

const MONTH = ['jan', 'feb', 'mar', 'apr', 'may', 'jun', 'jul', 'aug', 'sep', 'oct', 'nov', 'dec'];
const MONTH_WORD = /^(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?$/i;
const monthOf = (s) => (MONTH_WORD.test(s) ? MONTH.indexOf(String(s).slice(0, 3).toLowerCase()) + 1 : 0);
const pad = (n, w) => String(n).padStart(w, '0');
// A year written with two digits reads as 20yy (Mar-24 is March 2024, the way Excel and Power BI format it).
const year4 = (y) => (y.length === 2 ? `20${y}` : y);

/** A month named in full is drawn as its three letters on an axis ("September" is "Sep"): long names touched at September and December
 *  (UAT 17 Oct, round 3). Any other label, a month with its year included, is left as it is. */
export function axisLabelText(value) {
  const s = String(value ?? '');
  return /^(?:january|february|march|april|june|july|august|september|october|november|december)$/i.test(s) ? s.slice(0, 3) : s;
}

/** A sortable key for ONE label, or null when it is not a calendar label this reads. `named` says the column's own name
 *  already calls it a date, which is what lets a bare month name, a quarter or a year order. `calendarMonths: false`
 *  withholds the bare month name, because a fiscal year starts in a month the company chose and the label does not say. */
export function periodKey(value, named, { calendarMonths = true } = {}) {
  const s = String(value ?? '').trim();
  let m;
  if ((m = /^(\d{4})[-/ ]?(0[1-9]|1[0-2])$/.exec(s))) return `${m[1]}-${m[2]}`;                    // 2024-03, 2024/03, 202403
  if ((m = /^(\d{4})[-/ ]?[Qq]([1-4])$/.exec(s))) return `${m[1]}-Q${m[2]}`;                         // 2024-Q3, 2024Q3
  if ((m = /^[Qq]([1-4])[-/ ,]*(\d{4})$/.exec(s))) return `${m[2]}-Q${m[1]}`;                        // Q3 2024
  if ((m = /^(\d{4})[-/ ]?[Ww](\d{1,2})$/.exec(s))) return `${m[1]}-W${pad(m[2], 2)}`;             // 2024-W07
  if ((m = /^([A-Za-z]+)\.?[ ,-]+(\d{4})$/.exec(s)) && monthOf(m[1])) return `${m[2]}-${pad(monthOf(m[1]), 2)}`;   // Mar 2024
  if ((m = /^(\d{4})[ ,-]+([A-Za-z]+)\.?$/.exec(s)) && monthOf(m[2])) return `${m[1]}-${pad(monthOf(m[2]), 2)}`;   // 2024 Mar
  if ((m = /^([A-Za-z]+)\.?[-' ]+(\d{2})$/.exec(s)) && monthOf(m[1])) return `${year4(m[2])}-${pad(monthOf(m[1]), 2)}`;   // Mar-24, Jan 24, Dec'24
  if ((m = /^(0?[1-9]|1[0-2])[-/.](\d{4})$/.exec(s))) return `${m[2]}-${pad(m[1], 2)}`;                          // 03/2024, 3/2024
  if ((m = /^FY[ -]?(\d{4}|\d{2})[ ,_-]*([PpMm])(\d{1,2})$/i.exec(s)) && +m[3] >= 1 && +m[3] <= (m[2].toLowerCase() === 'm' ? 12 : 13)) return `FY${year4(m[1])}-P${pad(m[3], 2)}`;   // FY25 P3, FY2025 M03
  if ((m = /^FY[ -]?(\d{4}|\d{2})[ ,_-]*Q([1-4])$/i.exec(s))) return `FY${year4(m[1])}-Q${m[2]}`;                 // FY25 Q3
  if ((m = /^Q([1-4])[ ,_-]*FY[ -]?(\d{4}|\d{2})$/i.exec(s))) return `FY${year4(m[2])}-Q${m[1]}`;                 // Q3 FY25
  if ((m = /^FY[ -]?(\d{4}|\d{2})$/i.exec(s))) return `FY${year4(m[1])}`;                                         // FY25
  if (!named) return null;
  if (monthOf(s)) return calendarMonths ? pad(monthOf(s), 2) : null;                                                         // March
  if ((m = /^[Qq]([1-4])$/.exec(s))) return `Q${m[1]}`;                                              // Q3
  if (/^\d{4}$/.test(s)) return s;                                                                   // 2024
  return null;
}

/** The sortable key of every distinct label in a column (blanks left out), or null when ANY label is not one this reads.
 *  All keys must be the same shape, so a column mixing 2024-03 with a bare month cannot be put in one false order. */
export function periodKeys(values, named, opts) {
  const keys = new Map();
  let shape = null;
  for (const v of values) {
    if (v === null || v === undefined || v === '') continue;
    const k = periodKey(v, named, opts);
    if (k === null) return null;
    const sh = k.replace(/\d/g, '9');
    if (shape === null) shape = sh; else if (shape !== sh) return null;
    keys.set(String(v), k);
  }
  return keys.size ? keys : null;
}
