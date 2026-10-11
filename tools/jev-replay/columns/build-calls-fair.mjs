// Build the third run's Jev calls from gather-fair.py's facts file (README, "Third run"). Laptop only; reads and
// writes the private cache dir.
//
//   node build-calls-fair.mjs --in <facts file> --out <dir>
//
// Three call lists, one arm each ("fair"), written as <dir>/main/calls.json, <dir>/sort/calls.json and
// <dir>/format/calls.json so ask.ts can run each into its own folder:
//   main:   one call per column: hidden, summarize by (numbers), data category (text and numbers) and format (the
//           FMT-COLUMN population), with the facts the principled rules read, including the column's OWN
//           used-as-sort-target fact.
//   sort:   one call per text column with a one-to-one partner: "needs a sort" (a statement) and "which partner"
//           (a choice among the partners). No column's sort setting and no sort-target fact is in it, so it cannot
//           read the answer off the model.
//   format: the shipped column.format point's own question and state (names and types only), for the re-check of
//           the point already shipped.
// Every call carries the fair key's label (truth) and the principled rule's answer (rule) BESIDE the state, never
// in it. The state is names, types, structure and derived facts only: never a value and never any of the five
// settings of any column.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import {
  NUMERIC, DATACAT_CRITERIA, FORMAT_CRITERIA, baselineDatacatTyped, baselineFormatTyped, fairRuleHidden,
  fairRuleSummarize, fairRuleNeedsSort, fairRulePartner, nonAdditiveDimName,
} from './rules.mjs';

export const HIDDEN_FAIR = {
  yes: 'the column is a key used in a relationship, an id or technical column, or a helper that only orders another column',
  no: 'the column is something a report author reads, filters or groups by',
};

export const SUMMARIZE_FAIR = {
  sum: 'additive amounts and quantities, where a total means something',
  none: 'keys, ids, codes, years, period numbers and other labels stored as numbers, where a total means nothing',
  average: 'rates, ratios, percentages, prices, scores and measurements, where the mean means something and the total does not',
  count: 'the number of rows is counted',
  distinctcount: 'the number of different values is counted',
  min: 'the smallest value is taken',
  max: 'the largest value is taken',
};

const NEEDS_SORT = {
  yes: 'the values have a natural order that is not alphabetical (month or day names, quarters or periods, ordered bands such as 0-10, 11-20, 100+, ranked words such as Low, Medium, High, or dates stored as text), and a one-to-one partner column can supply that order',
  no: 'alphabetical order is already right for these values, or no partner column supplies a better order',
};

/** FMT-COLUMN's population (ReadinessRuleSet.ColumnFormatApplies), as the model stands now. */
export function formatPopulation(c) {
  return !c.isHidden && !c.isKey && (c.relationshipRole ?? 'none') === 'none' && !nonAdditiveDimName(c.name)
    && (NUMERIC.has(c.dataType) || c.dataType === 'dateTime');
}

function facts(t, c, { sortTarget }) {
  const m = { ...(c.measured ?? {}) };
  delete m.oneToOneWith; delete m.dictionaryBytes; delete m.dataBytes;
  const f = {
    table_rows: t.rows ?? null, is_key: !!c.isKey, relationship_role: c.relationshipRole,
    used_by_measures: c.usedByMeasures, measured: m,
    one_to_one_partners: (c.measured?.oneToOneWith ?? []).map((n) => ({ name: n, data_type: t.columns.find((x) => x.name === n)?.dataType ?? 'unknown' })),
  };
  if (sortTarget) f.used_as_sort_target = !!c.usedAsSortTarget;
  return f;
}

export function buildFairCalls(models) {
  const main = [], sort = [], format = [];
  for (const model of models) {
    const tableShape = model.tables.map((t) => ({ table: t.name, columns: t.columns.length, measures: t.measures ?? 0 }));
    for (const t of model.tables) {
      if (t.calculationGroup) continue;
      for (const c of t.columns) {
        if (!c.measured || !c.key) continue;
        const id = `${model.model}|${t.name}|${c.name}`;
        const base = {
          model_tables: tableShape,
          column: { table: t.name, name: c.name, data_type: c.dataType, is_a_calculated_column: !!c.isCalculated },
          other_column_names_in_the_same_table: t.columns.filter((x) => x !== c).map((x) => x.name),
          this_column_is_used_in_a_relationship: c.relationshipRole !== 'none',
          measure_count_in_the_same_table: t.measures ?? 0,
        };
        const qs = {}, truth = {}, rule = {}, author = {};
        qs.hidden = { type: 'noul', instructions: 'Good modelling practice hides this column from people building reports.', criteria: HIDDEN_FAIR };
        truth.hidden = c.key.hidden.label; rule.hidden = fairRuleHidden(c); author.hidden = !!c.isHidden;
        if (c.key.summarize_by) {
          qs.summarize_by = { type: 'choice', instructions: 'Following good modelling practice, how should this column\'s values be combined by default when a report author drops it onto a chart?', criteria: SUMMARIZE_FAIR };
          truth.summarize_by = c.key.summarize_by.label; rule.summarize_by = fairRuleSummarize(c);
          author.summarize_by = !c.summarizeBy || c.summarizeBy === 'default' ? 'sum' : c.summarizeBy;
        }
        if (c.key.data_category) {
          qs.data_category = { type: 'choice', instructions: 'What kind of real-world thing do the values in this column hold? Answer none unless the values clearly are that kind of thing.', criteria: DATACAT_CRITERIA };
          truth.data_category = c.key.data_category.label; rule.data_category = baselineDatacatTyped(c);
          author.data_category = c.dataCategory && c.dataCategory !== 'Uncategorized' ? c.dataCategory : 'none';
        }
        const fmt = c.key.format_family && formatPopulation(c);
        if (fmt) {
          qs.format_family = { type: 'choice', instructions: "How should this column's values be displayed to a report reader?", criteria: FORMAT_CRITERIA };
          truth.format_family = c.key.format_family.label; rule.format_family = baselineFormatTyped(c.name, c.dataType);
        }
        main.push({ id, model: model.model, questions: qs, truth, rule, author, why: whyOf(c.key), arms: { fair: { ...base, derived_facts: facts(t, c, { sortTarget: true }) } } });

        if (c.key.sort && (c.measured.oneToOneWith ?? []).length) {
          const partners = c.measured.oneToOneWith;
          const crit = Object.fromEntries(partners.map((p) => {
            const dt = t.columns.find((x) => x.name === p)?.dataType ?? 'unknown';
            return [p, `the column named "${p}" (${dt}) in the same table puts this column's values in their natural order`];
          }));
          sort.push({
            id, model: model.model,
            questions: {
              needs_sort: { type: 'noul', instructions: "This column should be sorted by another column, because alphabetical order is not its natural order.", criteria: NEEDS_SORT },
              sort_partner: { type: 'choice', instructions: 'If this column is sorted by another column, which one-to-one partner puts its values in their natural order?', criteria: crit },
            },
            truth: { needs_sort: c.key.sort.needs, sort_partner: c.key.sort.partners },
            rule: { needs_sort: fairRuleNeedsSort(c, t.columns), sort_partner: fairRulePartner(c, t.columns) },
            author: { needs_sort: !!c.sortByColumn, sort_partner: c.sortByColumn ?? 'none' },
            why: { sort: c.key.sort.why },
            arms: { fair: { ...base, derived_facts: facts(t, c, { sortTarget: false }) } },
          });
        }
        if (fmt) {
          // The shipped point's snapshot (ColumnFormatFamily.Snapshot): names and types only.
          format.push({
            id, model: model.model,
            questions: { format_family: { type: 'choice', instructions: "How are this column's values displayed to a report reader?", criteria: FORMAT_CRITERIA } },
            truth: { format_family: c.key.format_family.label }, rule: { format_family: baselineFormatTyped(c.name, c.dataType) },
            arms: { fair: {
              table_name: t.name, column_name: c.name,
              column_facts: { data_type: c.dataType, is_calculated: !!c.isCalculated },
              table_columns: t.columns.filter((x) => x !== c).sort((a, b) => a.name.localeCompare(b.name)).slice(0, 40).map((x) => ({ name: x.name, data_type: x.dataType })),
            } },
          });
        }
      }
    }
  }
  return { main, sort, format };
}

const whyOf = (k) => Object.fromEntries(Object.entries(k).map(([s, v]) => [s, v.why]));

if (import.meta.url === `file://${process.argv[1]}`) {
  const arg = (k) => { const i = process.argv.indexOf(k); return i > 0 ? process.argv[i + 1] : null; };
  const IN = arg('--in'), OUT = arg('--out');
  if (!IN || !OUT) throw new Error('usage: build-calls-fair.mjs --in <facts file> --out <dir>');
  const { main, sort, format } = buildFairCalls(JSON.parse(readFileSync(IN, 'utf8')).models);
  for (const [name, list] of Object.entries({ main, sort, format })) {
    mkdirSync(join(OUT, name), { recursive: true });
    writeFileSync(join(OUT, name, 'calls.json'), JSON.stringify(list));
  }
  const q = (l) => l.reduce((a, c) => { for (const k of Object.keys(c.questions)) a[k] = (a[k] ?? 0) + 1; return a; }, {});
  console.log(JSON.stringify({ main: main.length, sort: sort.length, format: format.length, questions: { ...q(main), ...q(sort) } }));
}
