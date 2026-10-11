# Column replay: does Jev with local facts beat the simple rules?

The evidence run that gates the Columns family (T268). It asks Jev the five column settings
(summarize by, hidden, data category, sort by, format) on real models, once with the contract's
derived facts and once with names only, and scores both against the trivial rules that
`docs/redesign/jev-pilot-verdict.md` defines. Results go in `docs/redesign/jev-columns-replay.md`,
aggregate numbers only.

Plain Node 26. No packages. Nothing in this folder holds a name from a real model, and nothing it
writes into the repository does either.

## The three steps

**1. Gather, on the machine that holds the connections.** Copy `gather.mjs`, `derive.mjs`,
`gazetteer.mjs` and `mcp-client.mjs` to `~/.cache/jev-replay-columns/` there, then run this in a
terminal inside the desktop session (not over SSH):

```
node ~/.cache/jev-replay-columns/gather.mjs
```

It starts the installed engine headless (`Semanticus.Engine mcp`) on a scratch workspace of its
own, so it never attaches to an engine a VS Code window is using. It connects read-only
(`connect_xmla`) to every saved xmla connection, reads metadata with `run_dmv`, and measures each
column with one scalar `run_dax` probe that returns counts only. It writes one file,
`~/Downloads/jev-columns-facts.json`: table and column names, data types, the five settings, and
derived facts. It prints counts only.

Why a desktop terminal: the engine's saved XMLA sign-in lives in the desktop session's keyring.
Over SSH the keyring cannot be reached, and Azure CLI tokens are refused by the XMLA endpoint, so
the connect fails. The connect is agent-origin, so it never opens a browser or a sign-in window;
if there is no saved sign-in it fails and the script moves on.

**2. Ask Jev, on the laptop.** Put the facts file in `~/.cache/jev-replay-columns/`, then:

```
node tools/jev-replay/columns/build-calls.mjs
JEV_REPLAY_LIVE=1 node --experimental-strip-types tools/jev-replay/columns/ask.ts
```

`ask.ts` is the pilot's own client (`docs/redesign/jev-pilot/run.ts`): same endpoint, model,
20 second timeout, concurrency 6, one retry. It refuses unless `JEV_REPLAY_LIVE` is exactly `1`,
refuses paths inside the repository, and reads the key from `~/.config/typesafe/env` into the
process only. It is a `.ts` file on purpose, like the pilot's runner, so the replay gate's
no-network scan (which reads `.mjs` files) stays about the default path.

**3. Score.**

```
node tools/jev-replay/columns/score.mjs
```

Writes `score.json` and `score.md` into the cache dir. Both are aggregate: no model, table or
column name, and sort-by confusions are reported as categories.

## What each file does

| file | job |
|---|---|
| `gather.mjs` | drives the engine over MCP, writes the facts file |
| `derive.mjs` | metadata shaping and the scalar DAX that yields derived facts |
| `gazetteer.mjs` | small country, region and city lists for `placeShare` |
| `mcp-client.mjs` | a minimal MCP stdio client |
| `rules.mjs` | truth derivations and the pilot's rule baselines, ported from `prep.py` |
| `build-calls.mjs` | one call per column, two arms, same questions and options |
| `ask.ts` | posts the calls to Jev |
| `score.mjs` | accuracy, calibration, adjudication, verdicts |
| `test/` | the self-test and a fake engine |

## Self-test

```
node --test tools/jev-replay/columns/test/columns.test.mjs
```

No network and no key. It runs `gather.mjs` against a fake engine and checks that every DAX query
is a scalar probe and that no connection string reaches the output. It checks that no call state
carries any of the five settings. It then scores a synthetic Contoso-style facts file end to end
and checks that nothing written names a table or column.

## Rules the run keeps

- Only names, types, settings and derived facts leave the machine that holds the data. The DAX
  returns counts, never a row, a raw value, a minimum, a maximum or a sample.
- The state Jev sees never includes the five settings for any column, which is the pilot's
  vocabulary rule. Both arms get the same options, and they are the options the engine would
  list: census enums, and for sort by, the table's one-to-one columns.
- Power BI's automatic date tables and row-number columns are left out. They are generated,
  not chosen by an author.

## Second run (26 September): summarize by, data category and sort by

The first run left these three on MORE DATA: one private model had one value of summarize by, one data category
and two sort-bys. The second run adds labels from public sample models and fixes its populations, rules and bars
here, in the same commit as the tools, before any Jev answer exists.

**Sources.** 54 public `.pbix` sample models from three public repositories, pinned by SHA-256 in
`second-run-sources.txt` (anonymous keys p0 to p53, no file names). They were chosen by one mechanical rule: every
`.pbix` that decodes, less near copies (a model sharing more than 60% of its table and column names with a larger
one already kept). `gather-pbix.py` reads them on this laptop with the pbixray package and writes the same facts file
`gather.mjs` writes; the facts are computed in Python field for field as `derive.mjs` computes them in DAX. Nothing
leaves the laptop but names, types, settings and derived facts. The first run's private model is not re-asked.

```
python3 tools/jev-replay/columns/gather-pbix.py --sources <path list> --out ~/.cache/jev-replay-columns-more/jev-columns-facts.json
node tools/jev-replay/columns/build-calls.mjs --in <that file> --out ~/.cache/jev-replay-columns-more/calls.json
JEV_REPLAY_CALLS=~/.cache/jev-replay-columns-more/calls.json JEV_REPLAY_OUT=~/.cache/jev-replay-columns-more \
  JEV_REPLAY_LIVE=1 node --experimental-strip-types tools/jev-replay/columns/ask.ts
node tools/jev-replay/columns/score.mjs --second --dir ~/.cache/jev-replay-columns-more
```

**Labels found (1,832 measured columns, before any Jev answer).** Summarize by: 874 number columns, labelled none
361, sum 420, count 54, average 39. Data category: 1,719 text and number columns, 87 carrying a category Power BI
offers, 4 carrying one it does not (left out and counted). Sort by: 394 text columns with a one-to-one partner, 59
of them sorted.

**Populations.** Summarize by: every number column. Data category: text and number columns; `Uncategorized` reads
as none. Sort by: text columns with at least one one-to-one partner (no labelled sort-by in either run sits on a
number or date column, and the engine offers only one-to-one partners).

**Rules.** Each question is judged against the stronger of the pilot's rule and a second rule that reads the same
derived facts the facts arm sees (`rules.mjs`, `baseline*Typed`): summarize by says none for a relationship column,
a key, a year, month number or quarter, or a column unique on every row; data category reads a value share of 0.8
or more (image link, web link, country, region, city; a postal code also needs the name); sort by sorts a month or
day name by its numeric one-to-one partner. Hidden and format are asked too and reported, but their verdicts stand
(hidden RULE, format SHIP).

**Bar (`score.mjs`, `BAR` and `BAR2`).** Per arm, as the first run: SHIP when the arm beats the stronger rule by 5
points or more on 50 or more columns with an exact McNemar p below 0.05, RULE when it does not beat the rule, MORE
DATA otherwise, and the more cautious of raw and adjudicated stands. Added: data category and sort by need 50 or
more labelled columns. Data category is judged on a balanced draw (every labelled column plus as many unlabelled
ones, seed 20260926) and must also be no worse than the rule on every column, or it is RULE. A setting ships with
the arm that clears the bar, and with the more accurate arm when both do. Only a SHIP becomes a product point.

## Third run (26 September): a fair re-test against a correct answer key

Kane challenged the second run: it graded Jev against what each sample's author happened to leave, not against what
is correct, and it asked lumped questions. The third run fixes both, on fresh models. Everything below, the key
included, was fixed and committed before any Jev answer existed.

**Fresh models.** 218 public `.pbix` sample models from 39 public repositories, pinned by SHA-256 in
`third-run-sources.txt` (keys f0 to f217, no file names). The list of repositories and commits they came from is kept
outside the repository, beside the private replay cache (26 Sep: it named the repositories' owners, several of them
people); a file is proved to be the one scored by its SHA-256. `census-fair.py` keeps every file that decodes, less any
file already used (by SHA-256) and any near copy (more than 60% shared table and column names) of a used model or of
a larger fresh one. 5,521 columns measured.

**The answer key (`fairkey.py`, run inside `gather-fair.py`).** It says what is CORRECT, from the column's own data
and the model's structure. The author's setting decides a row only where the data cannot. The values are read on
this laptop and dropped; only labels and reason codes are written, to the private cache dir.

- Data category: a category when 90% or more of the distinct values are that kind of thing, checked against
  reference lists independent of the product's gazetteer (pycountry, geonamescache). Postal code, address and county
  also need the name or the author (codes look like numbers, counties like surnames). Between 30% and 90%, the
  author's tag or the name decides if the values do not contradict it. Latitude and longitude need the name or the
  tag and values in range. Anything else is none, whatever the author tagged.
- Sort by: "needs a sort" is true for a text column when 90% or more of its values read as an ordered vocabulary
  (month or day names in five languages, quarters, periods, bands, ranked words, dates as text, numbers as text) whose
  natural order is not alphabetical, OR when a one-to-one partner named for ordering (Sort, Order, Rank) gives an
  order that is not alphabetical. "Which partner" is every one-to-one partner whose order matches the natural order,
  checked value by value. Ids and entity names never need a sort.
- Summarize by (numbers): none for relationship keys, id and code names, years, period numbers, row ids,
  coordinates, thresholds and dates stored as numbers; sum for counts and totals; average for rates, ratios, prices,
  scores, measurements and per-person durations; otherwise sum. An amount unique on every row is still summed.
- Hidden: foreign keys, id and surrogate keys, technical columns (row guids, load stamps, Power Query leftovers) and
  sort helpers are hidden; a date or a meaningful one-side key stays visible. Ambiguous, so the author decides: a sort
  target not named as a helper, and a number the table's measures already use.
- Format (the shipped point, re-checked): dates are dates; the author's currency stands; a percent name or the
  author's percent on values within -1.5 to 1.5 is percent; a money name is currency (the author's plain number
  stands); whole values are whole numbers; the rest is decimal.

**Hand check.** A seeded 10% of every setting's key was labelled BLIND by independent agents against a written rubric
(`handcheck/RUBRIC.md` in the cache dir), seeing the evidence and up to eight sample values but never the key. The
first check found classes of wrong key label; they were fixed (each class has a test in `test/test_fairkey.py`) and a
second, fresh 10% sample (no overlap) measured the fixed key. Both agreement rates are in the results note.

**The fair question (`build-calls-fair.mjs`).** One arm. Jev gets the facts the principled rules read: names, types,
relationship role, is-key, used-by-measures, the column's own used-as-sort-target, looks like a year, month, quarter
or period number, unique on every row, whole numbers, value-kind shares, and its one-to-one partners with their types.
Never a value, and never any column's setting. Sort by is split in two, asked in a separate call that carries no sort
setting or sort-target fact at all: "needs a sort" (a statement) and "which partner" (a choice among the partners).
The shipped column.format point is also asked its own question with its own names-and-types state.

**The Pro path (`score-fair.mjs`).** Per setting: Jev's answer when sure, else the principled rule. Sure is the
point's bar, 0.85, read the product's way (a choice's confidence; for a statement, max(p, 1 - p)). "Needs a sort" is
the month and day rule OR a sure yes from Jev.

**The principled rules (`rules.mjs`, `fairRule*`).** The same written rules as the key, applied to what the product
can see (names, types, structure and the derived facts Jev also gets), with no values and no author fallback. Data
category and sort use the second run's stronger rules; format uses the type-aware rule.

**Bar.** Per setting, paired on the fair key: SHIP when the Pro path beats the principled rule by 5 points or more,
exact McNemar p below 0.05, on 50 or more columns. Otherwise the rule stays. "Which partner" is scored only where the
key says a sort is needed; "sort by, end to end" scores what the column ends up sorted by, and it is the setting's
verdict. The hand-labelled samples are scored too, against the hand labels, as a check that does not lean on the key.

**A limit known before any answer, stated so nobody mistakes it later.** The principled rule and the key share their
written logic; the key adds the values and, on ambiguous rows, the author. So on the key the rule already scores
99.7% on hidden, 97.4% on data category, 94.7% on summarize by, 93.5% on sort by end to end and 85.3% on format
(`score-fair.mjs` with no answers). The largest margin the Pro path could possibly win is 100% minus the rule, so
hidden (0.3 points) and data category (2.6 points) CANNOT reach the 5-point bar on the key, whatever Jev answers, and
summarize by (5.3) and sort by (6.5) can only if Jev is nearly perfect. That is the brief's design working as
written: where good practice is a mechanical rule the engine can apply, the rule is the answer. The blind hand labels
are the read that does not share the rule's code (the rule scores 86% on summarize by and 83% on format there), so:

- **Verdict:** the key's bar, as the brief sets it.
- **Second read:** the same bar on the hand labels of both samples (3,282 items).
- **When they disagree** (a setting clears one and not the other), it is not shipped on either alone; it goes to Kane
  as a decision with both numbers.

```
python3 census-fair.py                                   # in the private cache dir
python3 tools/jev-replay/columns/gather-fair.py --sources sources.txt --out <cache>/jev-columns-facts.json
node tools/jev-replay/columns/build-calls-fair.mjs --in <cache>/jev-columns-facts.json --out <cache>/run
for s in main sort format; do JEV_REPLAY_CALLS=<cache>/run/$s/calls.json JEV_REPLAY_OUT=<cache>/run/$s JEV_ARMS=fair \
  JEV_REPLAY_LIVE=1 node --experimental-strip-types tools/jev-replay/columns/ask.ts; done
node tools/jev-replay/columns/score-fair.mjs --dir <cache>/run --hand <cache>/handcheck/hand-labels.json
```

**The key as committed (before any Jev answer).** 218 models, 5,521 columns. Labels: hidden 1,452 yes and 4,069 no;
summarize by (2,353 numbers) none 1,036, sum 991, average 326; data category (4,969) none 4,565 and 404 with a
category (Country 145, City 97, StateOrProvince 59, PostalCode 27, Latitude 14, Longitude 14, ImageUrl 14, WebUrl 13,
Continent 9, Address 9, Barcode 2, County 1); sort (709 text columns with a partner) 81 need a sort; format (1,571 in
FMT-COLUMN's population) whole number 877, currency 289, decimal 201, date 158, percent 46. The authors themselves set a
data category on 28 columns and a sort-by on 35.

Hand-check agreement, blind labels against the key (`agree.py` in the cache dir):

| setting | first sample, key before its fixes | second, fresh sample, key as committed |
|---|---|---|
| hidden | 517 / 552 (93.7%) | 525 / 552 (95.1%) |
| summarize by | 182 / 235 (77.4%) | 204 / 235 (86.8%) |
| data category | 489 / 497 (98.4%) | 492 / 497 (99.0%) |
| format | 278 / 286 (97.2%) | 275 / 286 (96.2%) |
| sort | 69 / 71 (97.2%) | 70 / 71 (98.6%) |
| all | 1,535 / 1,641 (93.5%) | 1,566 / 1,641 (95.4%) |

Summarize by is the weakest part of the key: its misses are coded categories stored as numbers, 0/1 flags and
per-person durations, where careful people also differ.

SHA-256 of the private files this commit fixes (cache dir `~/.cache/jev-replay-columns-fair`):

```
0e8023ecc891cc7666b1c82651ffeefa0f9f96663e3e0fd5cf6b38b045f3c6a9  jev-columns-facts.json (facts and key)
bcab943fc07eeb6b0635e901ebe52aa5bf57400cddc73afd9650fa334f895101  run/main/calls.json
18eeb9a1c09c6fe36788715e42651d566f156ceff32fa5fd727cc3d7dcbaba6e  run/sort/calls.json
e056515bf26de91c3cb4d7cd4ae50d0362ec04e587f7ea42803802f316e6cef3  run/format/calls.json
e3de28766ce4a14e8b66303be30058dae624cdda98b742e6db70b9b44f4cff25  hand-labels-all.json (both hand samples)
```

**Kane's bar, 26 September 2026 (recorded after c55f4403. When it arrived the sort and format answers
were in and the sort list had already been scored once under the old bar, reading RULE; the main list's answers were
still coming and nothing else had been scored).** Kane set a new switch-on bar for every Jev decision, and it replaces the 5-point
margin: a setting goes on for Pro when the Pro path beats the rule, the win is not luck (exact McNemar p below 0.05),
and it makes no more harmful mistakes than the rule. The size of the win does not matter. Harmful means a wrong hide
(hiding a column that should show), a wrong sort partner (sorting by a column that does not give the natural order,
including sorting a column that needs no sort), or summing something that must not be summed (sum where the key says
none or average). `score-fair.mjs` reports both bars; Kane's is the verdict. It also lifts the ceiling above: with no
margin, hidden and data category can go on if the Pro path wins at all, significantly and without extra harm.

## Re-test on the question that ships (26 September, fixed before any answer)

Kane, 26 September: "Jev should set summarisation of columns off as a best practice. It should suggest an explicit base
measure aggregation instead", and "Yes, re-test". column.summarize now sets every number column it hears back on to
not add up, and its pick chooses the column's explicit base measure. Both column points are re-tested on exactly the
question the product sends: one question per call, the product's payload (`product-payload.mjs`, pinned to the
engine's own payload by `test/equivalence.test.mjs` and `Semanticus.Tests/ColumnFamiliesFixTests.cs`).

- **Held out.** Every summarize and data-category column of the 218 pinned models except the first blind hand-check
  sample, which the answer key's fixes were fitted to: 2,118 summarize and 4,472 data-category calls.
- **Labels, both fixed before this run.** The fair answer key (its summarize_by label, the aggregation that makes
  sense, is what the base measure takes) and the second blind hand-check sample (235 summarize, 497 data category;
  fresh, never used to tune anything). One disclosure: choosing the flag guard's name-only form looked at the hand
  labels of 4 flag columns across both samples.
- **Pro path, the product's.** Jev when sure (0.85), else the product's rule (`productRuleSummarize`: the fair rule
  plus the flag guard; `baselineDatacatTyped` for data category). A sure pick the product refuses counts as its outcome.
- **Bar (`retest-bar.json`).** Kane's bar on the blind hand read (the Pro path beats the rule, exact McNemar p < 0.05,
  no more harmful mistakes), and no more harmful mistakes than the rule on the key read. A point that fails switches
  off and the rules decide.
- **Digests (`node retest.mjs build`).** summarize calls 05a0358fbd525a8d339dbcc950dd0cb2330abb7cb77e59ea78c98d88e177e364;
  data category calls 8aa6426f08a83742b50e5151a9d346abfd215015a4d2b13c0774f21b9e00b69e; labels (6,590 items)
  400dc30fbe0e9948077d13c79c196fcc5c5a3dc7986f1a3cc37b61521f0b2e0a.
- **Run.** `JEV_REPLAY_LIVE=1 JEV_ARMS=product JEV_REPLAY_CALLS=<cache>/<point>/calls.json JEV_REPLAY_OUT=<cache>/<point>
  node --experimental-strip-types ask.ts`, then `node retest.mjs score`.
- **Result (after the run; `node retest.mjs score`).** column.summarize ON: hand 90.6% vs 84.3%, 19/4, p 0.0026, harmful
  14/29; key 90.3% vs 93.9%, harmful 30/56. column.category OFF: hand 98.4% vs 97.4%, 7/2, p 0.18; key 98.4% vs 97.5%,
  53/12, p 3e-7. 6,583 of 6,590 calls answered, 8,364,298 input tokens, USD 0.351 (the two ask.log files).

