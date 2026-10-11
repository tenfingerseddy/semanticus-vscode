// Round 3 of the date.table replay: the answer key worked out from what is CORRECT for each model's structure and
// data, not from a reviewer's call (the coordinator's retest rules, 26 September).
//
//   node tools/jev-replay/datetable/derive-key.mjs      writes key.json beside this file
//
// THE DATA. These are Contoso-style models with no rows, so their data is declared: each model's `facts` in the label
// files, plus the declarations below for every date column the labels left unmeasured. A column is 'day' (one row per
// day, none missing), 'repeats' (a date on more than one row) or 'gaps' (no repeats, a day missing). The default for a
// column nothing declares is 'repeats': an event or attribute date. The exceptions are a relationship's one-side
// date column and a CALENDAR table's Date (a date key and a calendar are one row per day), and the date dimensions
// listed in DAY. In round 3 Jev is given exactly these facts, the ones the key is built from.
//
// THE KEY, three decisions apart:
//   needed    a fact table (the many side of a relationship, or a table with no relationships that a measure is
//             defined on) has a date column or a whole-number column that relates to a date table, or the model's
//             formulas use time intelligence;
//   table     among tables off the many side whose one 'day' column makes them a valid date table: the one the facts
//             relate through, else the one time intelligence reads, else the only one; none valid means generate.
//             Asked only when needed;
//   mark      yes whenever a date table is used: a date table that is not marked is never the correct end state.

import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const dir = dirname(fileURLToPath(import.meta.url));

/** Date dimensions whose date column is not a relationship's one side and not a CALENDAR table's Date. */
const DAY = new Set([
    'ns-1:Date/Date', 'ns-2:Date/Date', 'ns-3:Date/Date', 'ns-4:Date/Date', 'ns-7:Period/Date', 'ns-8:Period/Date',
    's-8:Fiscal Calendar/Fiscal Date', 's-13:DimDate/FullDate', 's-14:Calendar/Date', 'h-28:DateTable/Date',
]);

/** Models whose only dates are whole-number keys (20240131) on a fact that relates to no date table. Whether they are
 *  dates is a fact about the values the engine does not measure yet, so neither the key nor Jev can see it: these are
 *  reported apart and count for nobody. */
const UNVERIFIABLE = new Set(['ns-5', 'ns-6', 'h-20']);

/** Dates declared missing a day where the labels left them unmeasured. */
const GAPS = new Set(['s-11:Holidays/Date']);

const TI = /\b(TOTALYTD|TOTALQTD|TOTALMTD|DATESYTD|DATESQTD|DATESMTD|SAMEPERIODLASTYEAR|DATEADD|PARALLELPERIOD|PREVIOUSYEAR|PREVIOUSQUARTER|PREVIOUSMONTH|PREVIOUSDAY|NEXTYEAR|NEXTQUARTER|NEXTMONTH|NEXTDAY|OPENINGBALANCEYEAR|OPENINGBALANCEQUARTER|OPENINGBALANCEMONTH|CLOSINGBALANCEYEAR|CLOSINGBALANCEQUARTER|CLOSINGBALANCEMONTH|FIRSTDATE|LASTDATE|STARTOFYEAR|STARTOFQUARTER|STARTOFMONTH|ENDOFYEAR|ENDOFQUARTER|ENDOFMONTH)\s*\(/i;

function dax(item) {
    return [...item.measures.map((m) => m.split('|')[2]), ...(item.calculationGroup?.items ?? []).map((i) => i[1])];
}

/** Column refs ("Table/Column") a formula names, as 'Table'[Column] or Table[Column]. */
function refs(expr, item) {
    const out = new Set();
    for (const t of item.tables) {
        for (const c of t.cols) {
            const col = c.split(':')[0];
            const q = t.name.replace(/'/g, "''");
            if (expr.includes(`'${q}'[${col}]`) || (!/\s/.test(t.name) && expr.includes(`${t.name}[${col}]`))) out.add(t.name + '/' + col);
        }
    }
    return out;
}

export function truthFacts(item) {
    const facts = {};
    const oneSide = new Set(item.relationships.map((r) => r.split('>')[1]));
    for (const t of item.tables) {
        for (const c of t.cols) {
            const [col, type] = c.split(':');
            if (type !== 'DateTime') continue;
            const ref = t.name + '/' + col;
            facts[ref] = item.facts[ref]
                ?? (GAPS.has(item.id + ':' + ref) ? 'gaps'
                    : DAY.has(item.id + ':' + ref) || oneSide.has(ref) || (t.calendar && col === 'Date') ? 'day'
                    : 'repeats');
        }
    }
    return facts;
}

export function deriveKey(item) {
    const facts = truthFacts(item);
    const rels = item.relationships.map((r) => r.split('>').map((e) => e.split('/')));
    const many = new Set(rels.map((r) => r[0][0]));
    const related = new Set(rels.flatMap((r) => [r[0][0], r[1][0]]));
    const formulas = dax(item);
    const usesTi = formulas.some((f) => TI.test(f));
    const tiReads = new Set(formulas.filter((f) => TI.test(f)).flatMap((f) => [...refs(f, item)]));
    const measuredOn = new Set(item.measures.map((m) => m.split('|')[0]));
    const table = (n) => item.tables.find((t) => t.name === n);
    const dateCols = (t) => t.cols.filter((c) => c.endsWith(':DateTime')).map((c) => c.split(':')[0]);

    const valid = item.tables.filter((t) => !many.has(t.name))
        .filter((t) => dateCols(t).filter((c) => facts[t.name + '/' + c] === 'day').length === 1);
    const facts_ = item.tables.filter((t) => many.has(t.name) || (!related.has(t.name) && measuredOn.has(t.name)));
    const factNames = new Set(facts_.map((t) => t.name));
    // A whole-number column on a fact that relates to a table with a valid date column: a date key.
    const dateKey = rels.some(([from, to]) => factNames.has(from[0]) && valid.some((v) => v.name === to[0]));
    const needed = usesTi || dateKey || facts_.some((t) => dateCols(t).length > 0);

    let pick = null;
    if (needed) {
        const through = valid.filter((v) => rels.some(([from, to]) => to[0] === v.name && factNames.has(from[0])));
        const readByTi = valid.filter((v) => dateCols(v).some((c) => tiReads.has(v.name + '/' + c)));
        pick = through.length === 1 ? through[0].name
            : readByTi.length === 1 ? readByTi[0].name
            : valid.length === 1 ? valid[0].name
            : valid.length === 0 ? 'generate'
            : null;   // ambiguous: reported apart
    }
    return { needed, table: needed ? pick : null, mark: needed ? true : null, facts, ambiguous: needed && pick === null,
        unverifiable: UNVERIFIABLE.has(item.id) };
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) {
    const items = ['labels.json', 'heldout-labels.json'].flatMap((f) => JSON.parse(readFileSync(join(dir, f), 'utf8')).items);
    const key = items.map((i) => ({ id: i.id, ...deriveKey(i) }));
    writeFileSync(join(dir, 'key.json'), JSON.stringify({ version: 1, round: 3, derivedBy: 'derive-key.mjs', items: key }, null, 1) + '\n');
    const hand = (i) => i.label.table;
    const agree = items.filter((i, n) => {
        const k = key[n];
        return (k.needed ? k.table : 'none') === hand(i);
    }).length;
    process.stdout.write(`key.json: ${key.length} models, ${key.filter((k) => k.ambiguous).length} ambiguous, ${key.filter((k) => k.unverifiable).length} unverifiable. Agrees with the round 1 and 2 hand labels on ${agree} of ${key.length}.\n`);
    for (const [n, i] of items.entries()) {
        const k = key[n];
        const got = k.needed ? k.table : 'none';
        if (got !== hand(i)) process.stdout.write(`  ${i.id}: hand ${hand(i)}, key ${got}\n`);
    }
}
