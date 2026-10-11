// How a measure's own format shows a number in a result grid (0.0068 as 0.7%, 36111492.86 as $36,111,492.86): sorting and
// filtering still see the number. Kept apart from the grid so it can be tested alone.
//
// The format is read the way the model's own format string is: sections split on a ";" outside quotes (positive;negative;zero),
// a "%" scales by 100 only as an operator, never inside "quotes" or after a backslash (0.0"%" is a literal sign on a number
// that is already in points), and the section that applies to THIS value is the one honoured. A format this reader does not
// fully understand is not guessed at: the cell shows the number as it always did (Sol 6.1, delta B6).

type Tok = { k: 'lit'; t: string } | { k: 'pct' } | { k: 'num'; t: string };

// What may stand unquoted next to the digits, as in Excel's own rules: a currency sign, signs, brackets of a negative, a space.
const SAFE_LITERAL = /[$\-+() ]/;

/** The sections of a format, split on a ";" that is neither quoted nor escaped. Null when a quote or escape never closes. */
function sectionsOf(format: string): string[] | null {
  const out: string[] = [];
  let cur = '';
  for (let i = 0; i < format.length; i++) {
    const c = format[i];
    if (c === '"') { const e = format.indexOf('"', i + 1); if (e < 0) return null; cur += format.slice(i, e + 1); i = e; }
    else if (c === '\\') { if (i + 1 >= format.length) return null; cur += c + format[++i]; }
    else if (c === ';') { out.push(cur); cur = ''; }
    else cur += c;
  }
  out.push(cur);
  return out;
}

/** One section as literal text, "%" operators and ONE run of digit placeholders. Null for anything else. */
function tokensOf(section: string): Tok[] | null {
  const toks: Tok[] = [];
  let numAt = -1;
  for (let i = 0; i < section.length; i++) {
    const c = section[i];
    if (c === '"') { const e = section.indexOf('"', i + 1); if (e < 0) return null; toks.push({ k: 'lit', t: section.slice(i + 1, e) }); i = e; }
    else if (c === '\\') { if (i + 1 >= section.length) return null; toks.push({ k: 'lit', t: section[++i] }); }
    else if (c === '[') {
      // Excel's locale and currency code, [$-en-US] or [$€-407]: it names a locale and shows at most the currency sign.
      const e = section.indexOf(']', i + 1);
      const code = e < 0 ? null : /^\$([^\]-]*)(?:-[\w-]+)?$/.exec(section.slice(i + 1, e));
      if (!code) return null;
      if (code[1]) toks.push({ k: 'lit', t: code[1] });
      i = e;
    }
    else if (c === '%') toks.push({ k: 'pct' });
    else if ('#0,.'.includes(c)) {
      const last = toks[toks.length - 1];
      if (last && last.k === 'num') last.t += c;
      else if (numAt >= 0) return null; // two runs of digits, such as 0.0 "x" 0.0
      else { numAt = toks.length; toks.push({ k: 'num', t: c }); }
    }
    else if (SAFE_LITERAL.test(c)) toks.push({ k: 'lit', t: c });
    else return null; // a colour, a condition, an exponent, a padding mark, a word: not read here
  }
  return numAt >= 0 ? toks : null;
}

/** A number as the model's own format string shows it, for a format this reader fully understands: a percent ("0.0%"), a currency
 *  ("\\$#,0.00;(\\$#,0.00)"), a thousands or decimals pattern ("#,0", "0.00"). Null for any other format and for one that is not
 *  fully understood, so the cell shows the number as it always did. */
export function formatNumber(v: number, format?: string | null): string | null {
  return render(v, format, false);
}

/** A number in a format that shows a percent sign ("0.0%;-0.0%;0.0%", "0%", 0.0"%") as the format says. Null for any other
 *  format, and for one that is not fully understood, so the cell shows the number as it always did. */
export function percentOf(v: number, format?: string | null): string | null {
  return render(v, format, true);
}

function render(v: number, format: string | null | undefined, percentOnly: boolean): string | null {
  if (!Number.isFinite(v) || !format) return null;
  const secs = sectionsOf(format);
  if (!secs) return null;
  // Excel's rule: one section serves every value (a minus is added); two split positive and zero from negative; three add zero.
  const negative = v < 0;
  const section = negative && secs.length >= 2 ? secs[1] : v === 0 && secs.length >= 3 ? secs[2] : secs[0];
  const toks = tokensOf(section);
  if (!toks) return null;
  const operators = toks.filter((t) => t.k === 'pct').length;
  const sign = toks.some((t) => t.k === 'lit' && t.t.includes('%'));
  if (percentOnly && !operators && !sign) return null;
  const run = (toks.find((t) => t.k === 'num') as { t: string } | undefined)?.t ?? '';
  const m = /^([#0,]*)(?:\.([#0]*))?$/.exec(run);
  if (!m || !/[#0]/.test(run) || /,$/.test(m[1])) return null; // a trailing comma divides by a thousand: not read here
  const decimals = m[2] ?? '';
  const scaled = Math.abs(v) * 100 ** operators;
  const number = scaled.toLocaleString('en-US', {
    minimumFractionDigits: (decimals.match(/0/g) ?? []).length, maximumFractionDigits: decimals.length, useGrouping: m[1].includes(','),
  });
  const text = toks.map((t) => (t.k === 'num' ? number : t.k === 'pct' ? '%' : t.t)).join('');
  return (negative && secs.length === 1 ? '-' : '') + text;
}

// ---- dates -------------------------------------------------------------------------------------------------------------------
// A date column's value arrives as text, "2025-01-01T00:00:00". It shows in the column's own format string when it has one and
// this reader fully understands it ("dd/MM/yyyy", "MMMM yyyy", "d MMM yyyy"), else as a plain short date ("1 Jan 2025"). A time is
// shown only when the value has one. Display only: the rows are never rewritten.

const ISO_DATE = /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2}))?(?:\.\d+)?)?(?:Z|[+-]\d{2}:?\d{2})?$/;
const MONTH_NAMES = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];
const DAY_NAMES = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const pad2 = (n: number) => String(n).padStart(2, '0');

interface Stamp { y: number; mo: number; d: number; h: number; mi: number; s: number; hasTime: boolean }

function stampOf(text: string): Stamp | null {
  const m = ISO_DATE.exec(text);
  if (!m) return null;
  const mo = Number(m[2]); const d = Number(m[3]);
  if (mo < 1 || mo > 12 || d < 1 || d > 31) return null;
  const h = Number(m[4] ?? 0); const mi = Number(m[5] ?? 0); const s = Number(m[6] ?? 0);
  return { y: Number(m[1]), mo, d, h, mi, s, hasTime: h !== 0 || mi !== 0 || s !== 0 };
}

function plainDate(t: Stamp): string {
  const day = `${t.d} ${MONTH_NAMES[t.mo - 1].slice(0, 3)} ${t.y}`;
  return t.hasTime ? `${day} ${pad2(t.h)}:${pad2(t.mi)}` : day;
}

const NAMED: Record<string, string> = {
  'long date': 'dddd, d MMMM yyyy', 'medium date': 'd MMM yyyy', 'short date': 'd MMM yyyy', 'general date': '',
  'long time': 'HH:mm:ss', 'medium time': 'h:mm tt', 'short time': 'HH:mm',
};

/** The date in the format string: null when the format holds no date part or one this reader does not know. */
function inFormat(t: Stamp, format: string): string | null {
  const named = NAMED[format.trim().toLowerCase()];
  if (named !== undefined) return named === '' ? plainDate(t) : inFormat(t, named);
  const weekday = new Date(Date.UTC(t.y, t.mo - 1, t.d)).getUTCDay();
  const hasClock = /[hHs]/.test(format.replace(/"[^"]*"|\\./g, ''));
  let out = '';
  let seenDate = false;
  let afterHour = false;
  for (let i = 0; i < format.length;) {
    const c = format[i];
    if (c === '"') { const e = format.indexOf('"', i + 1); if (e < 0) return null; out += format.slice(i + 1, e); i = e + 1; continue; }
    if (c === '\\') { if (i + 1 >= format.length) return null; out += format[i + 1]; i += 2; continue; }
    if ('-/.:, '.includes(c)) { out += c; i++; continue; }
    if (!/[A-Za-z]/.test(c)) return null;
    let n = 1;
    while (format[i + n] === c) n++;
    const run = format.slice(i, i + n);
    i += n;
    const month = c === 'M' || (c === 'm' && !hasClock) || (c === 'm' && hasClock && !afterHour);
    if (c === 'y') { out += n <= 2 ? pad2(t.y % 100) : String(t.y).padStart(4, '0'); seenDate = true; }
    else if (month) { out += n === 1 ? String(t.mo) : n === 2 ? pad2(t.mo) : n === 3 ? MONTH_NAMES[t.mo - 1].slice(0, 3) : MONTH_NAMES[t.mo - 1]; seenDate = true; }
    else if (c === 'd') { out += n === 1 ? String(t.d) : n === 2 ? pad2(t.d) : n === 3 ? DAY_NAMES[weekday].slice(0, 3) : DAY_NAMES[weekday]; seenDate = true; }
    else if (c === 'H') { out += n === 1 ? String(t.h) : pad2(t.h); afterHour = true; }
    else if (c === 'h') { const h12 = t.h % 12 === 0 ? 12 : t.h % 12; out += n === 1 ? String(h12) : pad2(h12); afterHour = true; }
    else if (c === 'm') out += n === 1 ? String(t.mi) : pad2(t.mi);
    else if (c === 's') out += n === 1 ? String(t.s) : pad2(t.s);
    else if (run.toLowerCase() === 'tt' || run.toLowerCase() === 'am' || run.toLowerCase() === 'a') out += t.h < 12 ? 'AM' : 'PM';
    else return null;
  }
  return seenDate || /[Hhs]/.test(format) ? out : null;
}

/** A date value as the column's format shows it, or as a plain short date when the column has none (a time only when the value
 *  has one). Null when the value is not a date, so the caller shows it as it came. */
export function dateText(v: unknown, format?: string | null): string | null {
  if (typeof v !== 'string') return null;
  const t = stampOf(v);
  if (!t) return null;
  return (format && format.trim() ? inFormat(t, format) : null) ?? plainDate(t);
}
