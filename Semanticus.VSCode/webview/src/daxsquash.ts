// Whether two pieces of DAX are the same query, ignoring layout. Line breaks, indents and runs of spaces between
// tokens do not matter (the editor and the engine's template lay text out differently), but a space INSIDE a text
// value ("A  B" against "A B"), a quoted table name or a bracketed name does: that changes which rows the query
// matches, so treating those two as equal let a real edit vanish and let the page claim the chips still described it
// (Sol 3.8). Comments are layout too: their whitespace collapses, and a quote inside one starts nothing.

/** The text with layout collapsed outside text values and names: a run of whitespace is one space, the ends are trimmed. */
export function squash(text: string | null | undefined): string {
  const s = text ?? '';
  let out = '';
  let i = 0;
  const copyUntil = (close: string, doubled: boolean) => {
    // Copy from the opening mark to its closing one, verbatim; a doubled mark inside ("" or ]]) is part of the value.
    out += s[i++];
    while (i < s.length) {
      if (s[i] === close) {
        if (doubled && s[i + 1] === close) { out += close + close; i += 2; continue; }
        out += s[i++];
        return;
      }
      out += s[i++];
    }
  };
  while (i < s.length) {
    const c = s[i];
    if (c === '"') copyUntil('"', true);
    else if (c === "'") copyUntil("'", true);
    else if (c === '[') copyUntil(']', true);
    else if ((c === '/' && s[i + 1] === '/') || (c === '-' && s[i + 1] === '-')) {
      const end = s.indexOf('\n', i);
      const stop = end < 0 ? s.length : end;
      out += s.slice(i, stop).replace(/\s+/g, ' ').trimEnd();
      i = stop;
    } else if (c === '/' && s[i + 1] === '*') {
      const end = s.indexOf('*/', i + 2);
      const stop = end < 0 ? s.length : end + 2;
      out += s.slice(i, stop).replace(/\s+/g, ' ');
      i = stop;
    } else if (/\s/.test(c)) {
      while (i < s.length && /\s/.test(s[i])) i++;
      out += ' ';
    } else out += s[i++];
  }
  return out.trim();
}
