// Pure facts-to-copy helpers for the Model Overview page. A plain ES module (the storagecalc.mjs pattern) so the
// node contract test runs the SAME code the webview ships, instead of asserting against the JSX source text.
//
// The rule every function here obeys: a clause exists only when its fact is real. The Overview is the landing page,
// so a placeholder sentence there ("no scan yet", "0 instructions") would read as a measurement. Nothing is
// rounded into existence: a missing scan produces a DIFFERENT band, not a band full of zeros.

/** The four table kinds the Overview colours by. Precedence matches Model home's existing label order:
 *  a field parameter is also a calculated table, and it must read as a field parameter. */
export function tableKind(table) {
  if (!table) return 'data';
  if (table.isFieldParameter) return 'fieldParameter';
  if (table.isCalculated) return 'calculated';
  if (table.isDateTable) return 'date';
  return 'data';
}

export const KIND_LABEL = {
  data: 'Data table', date: 'Date table', calculated: 'Calculated', fieldParameter: 'Field parameter',
};

const MB = 1048576;

/** Storage sizes read the way Size by table reads them, so one model never shows two different totals. */
export function formatBytes(bytes) {
  const n = Number(bytes) || 0;
  if (n >= MB) return (n / MB).toFixed(1) + ' MB';
  if (n >= 1024) return Math.round(n / 1024) + ' KB';
  return n + ' bytes';
}

/** Row counts in the hero tile: 2,325,450 rows reads as 2.3 M, and small counts stay exact. */
export function formatCount(n) {
  const v = Number(n) || 0;
  if (v >= 1000000) return (v / 1000000).toFixed(1) + ' M';
  if (v >= 10000) return Math.round(v / 1000) + ' K';
  return v.toLocaleString('en-US');
}

const plural = (n, one, many) => `${n.toLocaleString('en-US')} ${n === 1 ? one : many}`;

/** How many instructions are written for the assistant. One non-blank line is one instruction: that is how the
 *  text is authored and how it reads back, and it is a count of something really in the model, not an estimate. */
export function instructionCount(text) {
  if (typeof text !== 'string') return 0;
  return text.split(/\r?\n/).filter((line) => line.trim().length > 0).length;
}

/** The hero paragraph, as clauses. The caller sets the first one in the page's own voice (bold) and leaves the
 *  rest plain, so the shape of the sentence cannot drift apart from the shape of the facts.
 *
 *  `state` is how far the model graph has actually been read, and it is load-bearing: an UNREAD model and an
 *  EMPTY one used to print the same sentence. The loading and failed scenes both said "0 tables, 0 measures
 *  and 0 relationships" above their own status line, because the caller turned missing graph data into empty
 *  arrays before counting it. Zero is a measurement, so it is kept for a model that was read and really is
 *  empty (Astra spot review 2, follow-up, 2026-09-14). */
export function heroClauses(facts) {
  const f = facts || {};
  if (f.state === 'loading') return ['Reading the model…'];
  // A failed read already has its own panel directly below, naming the error and offering Try again. A second
  // sentence up here could only repeat it, or invent numbers nobody has.
  if (f.state === 'error') return [];
  const out = [];
  out.push(`${plural(f.tables || 0, 'table', 'tables')}, ${plural(f.measures || 0, 'measure', 'measures')} and ${plural(f.relationships || 0, 'relationship', 'relationships')}.`);

  const bytes = Number(f.memoryBytes) || 0;
  if (f.memoryBytes != null && bytes > 0) {
    const share = Math.round(Number(f.largestShare) || 0);
    out.push(f.largestTable && share > 0
      ? `The model uses ${formatBytes(bytes)} in memory and ${share}% of it sits in ${f.largestTable}.`
      : `The model uses ${formatBytes(bytes)} in memory.`);
  }

  const instructions = Number(f.instructionCount) || 0;
  if (instructions > 0) out.push(`Your assistant follows ${plural(instructions, 'instruction', 'instructions')} written for this model.`);

  // Edits SEEN BY THIS PAGE since the model opened. Not a destination comparison: the clause used to read
  // "waiting to be published", which claimed a fact no code here establishes (Astra spot review 2, finding 2).
  const edits = Number(f.sessionEdits) || 0;
  if (edits > 0) out.push(`${plural(edits, 'edit', 'edits')} this session.`);

  return out;
}

export function heroSentence(facts) { return heroClauses(facts).join(' '); }

/** The "Where the memory goes" band. With a scan the blocks are sized by memory; without one they are sized by
 *  column count and the caller says so in the caption. Shares always add up to 100 for a model with tables, so
 *  the band is one full-width bar rather than a bar with an unexplained gap at the end. */
export function memoryBand(input) {
  const tables = (input && input.tables) || [];
  const scan = (input && input.scanTables) || null;
  const sized = Array.isArray(scan) && scan.length > 0;
  const sizeOf = new Map((sized ? scan : []).map((t) => [t.name, Number(t.size) || 0]));
  const rowsOf = new Map((sized ? scan : []).map((t) => [t.name, t.rows == null ? null : Number(t.rows)]));

  const weighed = tables.map((t) => ({
    name: t.name,
    kind: tableKind(t),
    bytes: sized ? (sizeOf.get(t.name) || 0) : null,
    rows: sized ? (rowsOf.get(t.name) ?? null) : null,
    columns: Number(t.columns) || 0,
    weight: sized ? (sizeOf.get(t.name) || 0) : (Number(t.columns) || 0),
  }));
  const total = weighed.reduce((n, t) => n + t.weight, 0);
  // Every table is a block even at zero weight: a table with no measured size is still part of the model, and an
  // even split is the honest picture when nothing separates them yet.
  const blocks = weighed
    .map((t) => ({ ...t, share: total > 0 ? (100 * t.weight) / total : (weighed.length ? 100 / weighed.length : 0) }))
    .sort((a, b) => b.share - a.share || a.name.localeCompare(b.name));

  return { basis: sized ? 'memory' : 'columns', total, blocks };
}

/** The gap the band draws between two blocks, and the width a block too small to draw is reduced to. Both are
 *  also written in styles.css; they live here as well because the geometry below has to subtract them, and a
 *  test proves the two copies still agree. */
export const BAND_GAP_PX = 2;
export const BAND_SLIVER_PX = 2;
/** The padding the label carries INSIDE its block. The block itself has none. */
const BAND_LABEL_PAD_PX = 16;

/** Where each block actually starts and stops, in pixels, for a band of this width.
 *
 *  This exists because the band lied. Blocks were laid out as flex growth with 16px of horizontal padding on
 *  every block, and flex-basis: 0 does not remove padding from an item's base size: the padding came off the
 *  top before anything was shared out. Sales printed 66% and drew 762px of a 1330px band (57.3%) at 1440, and
 *  496px of 926px (53.6%) at 1000, while five tables measured at zero bytes each drew about 16.5px instead of
 *  a 2px sliver (Astra spot review 2, finding 3, measured 2026-09-14).
 *
 *  The padding now sits on an inner element, so this function models exactly what the browser does with the
 *  corrected CSS: take the gaps off the band, freeze every block too small to draw at the sliver width, and
 *  divide what is left by share. The loop is not decoration: freezing one block shrinks the pool, which can
 *  push the next one under the sliver too. */
export function bandGeometry(blocks, bandPx, options) {
  const gap = Number(options && options.gapPx) || BAND_GAP_PX;
  const sliver = Number(options && options.sliverPx) || BAND_SLIVER_PX;
  const list = (blocks || []).map((b) => ({ name: b && b.name, share: Number(b && b.share) || 0 }));
  if (list.length === 0) return [];

  const inner = Math.max(0, (Number(bandPx) || 0) - gap * (list.length - 1));
  const frozen = new Set();
  let widths = list.map(() => 0);
  for (;;) {
    const free = Math.max(0, inner - frozen.size * sliver);
    const pool = list.reduce((n, b, i) => (frozen.has(i) ? n : n + b.share), 0);
    const open = list.length - frozen.size;
    widths = list.map((b, i) => {
      if (frozen.has(i)) return sliver;
      return pool > 0 ? (free * b.share) / pool : free / open;
    });
    const next = widths.findIndex((w, i) => !frozen.has(i) && w < sliver);
    if (next === -1 || open === 1) break;
    frozen.add(next);
  }

  return list.map((b, i) => ({ name: b.name, share: b.share, px: widths[i], label: blockLabel(b.name, widths[i]) }));
}

/** How much of a block's label fits inside it, given the width that block really draws. Labels that do not fit
 *  are dropped rather than clipped, and the block keeps its tooltip, so a narrow viewport loses text instead of
 *  showing half a word. It takes PIXELS, not a share: re-deriving the width from a share is the arithmetic the
 *  renderer got wrong, and two places doing that sum differently is how the label and the block disagreed. */
export function blockLabel(name, widthPx) {
  const width = (Number(widthPx) || 0) - BAND_LABEL_PAD_PX;
  const nameWidth = String(name || '').length * 6.4;
  if (width >= nameWidth + 62) return 'full';
  if (width >= nameWidth) return 'name';
  return 'none';
}

/** One row per table for the Tables card. With a scan each row carries the three parts Size by table measures,
 *  plus the remainder the scanned columns do not account for — the split covers the top columns only, and hiding
 *  that gap would turn a partial measurement into a claim about the whole table. */
export function tableRows(input) {
  const tables = (input && input.tables) || [];
  const scan = (input && input.scanTables) || null;
  const columns = (input && input.scanColumns) || [];
  const sized = Array.isArray(scan) && scan.length > 0;
  const band = memoryBand({ tables, scanTables: scan });
  const byName = new Map(band.blocks.map((b) => [b.name, b]));

  const parts = new Map();
  if (sized) {
    for (const c of columns) {
      const p = parts.get(c.table) || { data: 0, dict: 0, hash: 0 };
      p.data += Number(c.dataSize) || 0;
      p.dict += Number(c.dictionarySize) || 0;
      p.hash += Number(c.hashIndexSize) || 0;
      parts.set(c.table, p);
    }
  }
  const total = band.total;
  const pct = (n) => (total > 0 ? (100 * n) / total : 0);

  return band.blocks.map((b) => {
    const source = tables.find((t) => t.name === b.name);
    const p = parts.get(b.name);
    const measured = p ? Math.min(p.data + p.dict + p.hash, b.bytes || 0) : 0;
    return {
      name: b.name,
      ref: source ? source.ref : null,
      kind: b.kind,
      measures: source ? Number(source.measures) || 0 : 0,
      columns: b.columns,
      bytes: b.bytes,
      rows: b.rows,
      share: b.share,
      // In the no-scan fallback the single bar is the table's share of the model's columns.
      segments: sized && p
        ? [
          { part: 'data', width: pct(p.data) },
          { part: 'dict', width: pct(p.dict) },
          { part: 'hash', width: pct(p.hash) },
          { part: 'rest', width: pct(Math.max(0, (b.bytes || 0) - measured)) },
        ]
        : [{ part: sized ? 'rest' : 'columns', width: b.share }],
    };
  });
}

/** The Checks health card, read from the most recent saved run. "Could not check" is never folded into a failure:
 *  an unverifiable test is an unknown, and the card says so in its own line. */
export function checksSummary(history) {
  const runs = (history && history.runs) || [];
  if (runs.length === 0) {
    return { state: 'none', headline: 'No tests run yet', detail: 'Run the tests to see what holds and what does not.' };
  }
  const health = runs[runs.length - 1].health || {};
  const checked = Number(health.checked) || 0;
  const passed = Number(health.passed) || 0;
  const failed = Number(health.failed) || 0;
  const unknown = (Number(health.notVerifiable) || 0) + (Number(health.missing) || 0);
  const headline = `${passed.toLocaleString('en-US')} of ${checked.toLocaleString('en-US')} pass`;
  // Failures lead, unknowns follow, and neither is folded into the other: a run with both used to report only
  // the unknown and hide five real failures. The run history does not carry WHY a check was unverifiable, so
  // this never guesses a reason; it points at Checks, where the reason is written.
  const parts = [];
  if (failed > 0) parts.push(`${plural(failed, 'check', 'checks')} did not pass.`);
  if (unknown > 0) parts.push(`${plural(unknown, 'check', 'checks')} could not be checked. Open Checks to see why.`);
  const detail = parts.length > 0 ? parts.join(' ') : 'Everything that was checked passed.';
  return { state: failed > 0 ? 'warn' : unknown > 0 ? 'unknown' : 'ok', headline, detail };
}

/** When a saved run was recorded, in the reader's own clock. Near dates get the word a person would use; older
 *  ones get the date, because "Tuesday" three weeks back names the wrong Tuesday. An unreadable timestamp
 *  returns null and the caller drops the phrase rather than printing a fallback that looks like a reading. */
export function recordedWhen(iso, now) {
  const at = new Date(iso);
  if (!iso || Number.isNaN(at.getTime())) return null;
  const clock = at.toLocaleTimeString('en-US', { hour: 'numeric', minute: '2-digit' });
  const today = now ? new Date(now) : new Date();
  const midnight = (d) => Date.UTC(d.getFullYear(), d.getMonth(), d.getDate());
  const days = Math.round((midnight(today) - midnight(at)) / 86400000);
  if (days === 0) return `today at ${clock}`;
  if (days === 1) return `yesterday at ${clock}`;
  if (days > 1 && days < 7) return `${at.toLocaleDateString('en-US', { weekday: 'long' })} at ${clock}`;
  return `${at.getDate()} ${at.toLocaleDateString('en-US', { month: 'long' })} at ${clock}`;
}

/** The Checks card, which shows ONE run and says which one.
 *
 *  The rule this encodes: the run a person just triggered from this page wins, and saved history is shown only
 *  when no run has happened here. The two are never blended. Run tests used to await the run, throw the result
 *  away and re-read the saved history; the RPC does not persist by default and the engine only appends history
 *  when it does, so the card answered a fresh run of 23 failures with the old "23 of 23 pass" (Astra spot review
 *  2, finding 1, reproduced 2026-09-14). Running checks is free and stays free: the fix reads the run that came
 *  back, it does not start asking for the paid persistence that would have made the old read correct.
 *
 *  `source` is the line that says which run you are looking at, and it is never empty for a run that exists. */
export function checksCard(input) {
  const latest = (input && input.latest) || null;
  const history = (input && input.history) || null;
  // A run that came back with no health measured nothing, so its numbers cannot be shown. Its error still
  // travels: the card says the run failed and keeps whatever recorded result it already had, under that
  // result's own time stamp, so the two can never be read as one.
  if (latest && latest.health) {
    return {
      ...checksSummary({ runs: [{ health: latest.health }] }),
      source: latest.persisted ? 'Just now, recorded' : 'Just now, not recorded',
      recorded: !!latest.persisted,
      note: latest.note || null,
      error: latest.error || null,
    };
  }
  const runs = (history && history.runs) || [];
  const summary = checksSummary(history);
  const error = (latest && latest.error) || null;
  const note = (latest && latest.note) || (history && history.note) || null;
  if (runs.length === 0) return { ...summary, source: null, recorded: false, note, error };
  const when = recordedWhen(runs[runs.length - 1].when);
  return { ...summary, source: when ? `Last recorded run, ${when}` : 'Last recorded run', recorded: true, note, error };
}

/** The Changes card. It describes the edits THIS PAGE HAS SEEN since the model opened, and nothing else.
 *
 *  It used to render "Nothing to publish / Every edit you have made is already published" whenever that local
 *  list was empty. The list starts empty, is filled only by this webview's own change notifications and is
 *  capped, so it is neither a destination comparison nor a record of unpublished work: a reload alone produced
 *  that claim on a model with unsaved edits (Astra spot review 2, finding 2). The comparison that could answer
 *  the publication question lives behind Review changes and the publish step, so this card names its own number
 *  and points there. */
export function changesCard(input) {
  const edits = Math.max(0, Number(input && input.edits) || 0);
  const capped = !!(input && input.capped);
  const known = 'This page does not compare the model with where it publishes.';
  if (edits === 0) {
    return { state: 'none', headline: 'No edits this session', detail: `Nothing has changed here since you opened this model. ${known}` };
  }
  const counted = capped
    ? `${edits.toLocaleString('en-US')} or more edits this session`
    : `${plural(edits, 'edit', 'edits')} this session`;
  return { state: 'info', headline: counted, detail: `Made since you opened this model here. ${known}` };
}
