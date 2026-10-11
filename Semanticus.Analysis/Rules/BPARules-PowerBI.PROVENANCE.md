# Provenance: `BPARules-PowerBI.json`

The rule file carries its own `//` attribution header, including MIT's full permission notice. JSON has
no comment syntax in the specification, but the reader for that file sets `JsonCommentHandling.Skip`
(`Semanticus.Analysis/BpaRuleSet.cs`), matching the Newtonsoft leniency Tabular Editor itself uses, so
the header parses. That matters because the file is an `EmbeddedResource`: it is compiled into
`Semanticus.Analysis.dll` and shipped in every `.vsix`, where no sidecar document follows it.

This document is the long-form record behind that header: the exact pin, every modification, and why.
The same facts are mirrored in the repository's `THIRD-PARTY-NOTICES.md` and `third-party-manifest.json`,
and the shipped `Semanticus.VSCode/NOTICE` carries the licence for binary recipients.

## Borrowed portion — 69 of 74 rules

Taken from Microsoft's Analysis Services repository. 69 of the source file's 71 rules ship; the two
omissions and two scope edits are listed under "Modifications" below.

| | |
|---|---|
| Repository | <https://github.com/microsoft/Analysis-Services> |
| Path | `BestPracticeRules/BPARules.json` |
| Commit | `50e8ce5028dd7046e73974ec69cb79af225e41b0` (2026-01-21) |
| Git blob SHA-1 | `5bf7ab2c25de948d841414d8f377b68f7effd5f7` |
| SHA-256 of the retrieved file | `ddb9cff4c2a0611a6467e2559d38319d9867381998066473ffa1e11c2d360392` |
| Size | 55,920 bytes |
| Rule count in the source file | 71 (69 shipped, see Modifications) |
| Licence | MIT, per the `LICENSE` file at the repository root |
| Copyright | `Copyright (c) 2016 Microsoft` (quoted exactly from that `LICENSE`) |

The `BestPracticeRules/` folder publishes no licence of its own and its `README.md` states no terms
contrary to the root MIT licence, so the root licence governs.

## The MIT licence, quoted in full

MIT requires the copyright notice **and** the permission notice to travel with copies, and naming
"MIT" is not that notice. It is therefore carried in three places, because each reaches a different
recipient: the rule file's own `//` header (which travels inside the compiled DLL), this document
beside it (published by the public source mirror, being in neither exclusion list in
`tools/release/mirror-manifest.json`), and `Semanticus.VSCode/NOTICE`, which is packaged as
`extension/NOTICE` and is the only notice a `.vsix` recipient receives.

Retrieved from `https://github.com/microsoft/Analysis-Services/blob/50e8ce5028dd7046e73974ec69cb79af225e41b0/LICENSE`
(1,066 bytes):

> MIT License
>
> Copyright (c) 2016 Microsoft
>
> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

The SHA-256 above is of the upstream file as retrieved, **not** of the file in this directory. Our
shipped file is 69 of the upstream rules (two of them scope-narrowed) with five Semanticus-authored
rules appended, so its hash necessarily differs. To re-verify the borrowed portion, fetch the upstream file at the pinned commit
and hash that.

## Modifications to the borrowed portion

MIT permits modification; these are ours and are recorded so the diff against upstream is never a
mystery. Each was found by running every rule through `RuleAuthoring.CheckBpa`, the same validator
behind `validate_rule` — not by inspection. The tripwire is
`Semanticus.Tests/BpaCorpusTests.Every_shipped_rule_compiles_and_runs`.

Our BPA evaluator supports a fixed scope vocabulary that does not include `TablePermission`,
`ProviderDataSource` or `StructuredDataSource`.

**Two rules omitted** — every scope they declare is one we cannot evaluate, so shipping them would put
two entries in the corpus that look like checks and silently never fire:

| Omitted rule | Declared scope |
|---|---|
| `CHECK_IF_DYNAMIC_ROW_LEVEL_SECURITY_(RLS)_IS_NECESSARY` | `TablePermission` |
| `REMOVE_DATA_SOURCES_NOT_REFERENCED_BY_ANY_PARTITIONS` | `ProviderDataSource`, `StructuredDataSource` |

**Two rules narrowed** — they keep working on the scopes we do support, with the unusable ones removed:

| Narrowed rule | Scope removed | Still evaluates |
|---|---|---|
| `DAX_COLUMNS_FULLY_QUALIFIED` | `TablePermission` | Measure, KPI, CalculationItem |
| `TRIM_OBJECT_NAMES` | `ProviderDataSource`, `StructuredDataSource` | 15 other scopes |

If our evaluator later learns those three scopes, the honest move is to restore all four upstream
rules unchanged and delete this section.

**DATA_COLUMNS_MUST_HAVE_A_SOURCE_COLUMN excludes calculation-group columns.** Upstream flags any
data column with an empty SourceColumn. Calculation-group columns are typed as data columns in TOM
but they are not loaded from a query, so a working Time Intelligence group named Time Calc was
treated as a processing error and blocked a live push (D-009). The expression now also requires
`Table.ObjectTypeName != "Calculation Group Table"`. A real imported column with no source still
fails. The check itself did not change for any other column.

**Documentation links removed from 29 fields.** The release-security gate treats every engine-owned
file as engine source and rejects any http or https host that is not on the engine's approved-endpoint
allowlist. That gate is what proves the engine makes no inference calls and holds no model-provider
credentials. Microsoft's descriptions carry 25 documentation links (sqlbi.com, elegantbi.com,
youtube.com and others). Adding those hosts to the allowlist would make the gate assert that the
engine may contact them, which is false, so the links were stripped and the gate left exactly as
strict. Where a link was mid-sentence the sentence now points at Microsoft's BestPracticeRules
documentation by name. No check changed behaviour: the removed text is prose, never an expression.

If we want those references back in front of users, the right place is a docs-side map from rule id to
reference URL, outside the engine boundary. That is not built.

**PERCENTAGE_FORMATTING FixExpression literals normalized.** Upstream stores the intended
semicolon-separated format string by writing a doubled backslash plus `u003B` in the JSON file, so the
parsed string contains the six-character sequence `\u003B` rather than U+003B. Our evaluator's
`Coerce` only strips quotes; it does not decode that sequence. `bpa_fix` therefore wrote
`#,0.0%\u003B-...` and the rule's Expression, which already compares against real semicolons, still
fired. The FixExpression in our copy now uses real `;` characters, matching the Expression. No other
rule changed. The upstream version pin did not move. (Since round 8 on 28 Sep this fix never applies; see "No fix
writes over a format string that is already there" below.)

**Four rules exempt field parameters (27 Sep).** `UNNECESSARY_COLUMNS`, `DAX_MEASURES_UNQUALIFIED`,
`REDUCE_USAGE_OF_CALCULATED_TABLES` and `ENSURE_TABLES_HAVE_RELATIONSHIPS` each carry a Semanticus-only
`"ExemptFieldParameters": true` field. A field parameter is exactly what Power BI Desktop builds for a field
switcher: a calculated table with no relationship, whose `NAMEOF ( 'Sales'[Net Sales] )` rows qualify the
measure, and whose hidden Fields column nothing references by name. All four rules fired on that shape and
none of the findings could be acted on without breaking the switcher. The evaluator (`BpaAnalyzer`) skips a
field parameter table and its columns for a rule with the flag, using the readiness rules' own detection
(`ReadinessRuleSet.IsFieldParameterTable`), and the readiness rule `DAC-FIELD-PARAM-COMPLEXITY` still reports
the real cost of one. The expressions themselves are unchanged. Tabular Editor ignores the unknown field, so
there a field parameter is flagged as before.

**`FORMAT_FLAG_COLUMNS_AS_YES/NO_VALUE_STRINGS` accepts a real Yes/No format string (27 Sep).** Upstream flags every
visible integer column named `Is...` whatever its format string, so a flag already shown as Yes or No through
`"Yes";"Yes";"No"` still failed, and only a data type change cleared it. The expression now clears a column whose
format string IS that three-part form (spaces ignored, case ignored, an optional fourth blank section allowed).
A first pass only asked that the string contain `"Yes"` and `"No"` somewhere, which cleared `"Yes";"No"` as well; with
two sections the first covers positive values AND zero (DAX FORMAT, MDX FORMAT_STRING and VBA Format all say so), so
a 0 flag read as Yes and the rule told a person their flag was formatted when it was not. The description now
explains the three parts, names the tool that writes them, and says why two parts are not enough.

A second pass (round 3, V2 of the verifier's report) removed `DataType = "Int64"` from that clearing clause. Upstream's
second branch catches any non-text column named `... Flag`, so a **Boolean** flag was flagged and the remedy the
description named could not clear it: the person was told to write a format string and nothing happened. Every flag the
rule catches now clears the same way, which is also how Analysis Services reads it (true counts as the first section and
false as the third, the reason Power BI's own default Boolean format string is `"TRUE";"TRUE";"FALSE"`). D1 is not
loosened: two sections still fail for a Boolean flag exactly as they do for an integer one.

A third pass (28 Sep, round 7, D4 of the verifier's round-6 report) added `DataType <> "DateTime"` to that second
branch. Upstream's `DataType <> "String"` let a DATE column named `... Flag` in, so a column such as `Renewal Flag`
carrying `Long Date` or `dd/mm/yyyy` was told to show itself as `"Yes";"Yes";"No"`, which a date cannot be (its serial
is always positive, so it would always read Yes). A date column is now never raised for its name, and the description
says so. The rule has no fix, so nothing was ever written; it was a false finding.

**`DATECOLUMN_FORMATSTRING` flags only a date column with no format string, and its fix writes ISO (27 Sep).**
Upstream's fix writes `mm/dd/yyyy`, which is a US reading and reads wrongly in Australia and most of the world, and
its expression passes only that one literal. The fix now writes `yyyy-mm-dd`. The expression now flags only a date
column with NO format string at all, so every real date format passes: Long Date, Short Date, `dd/mm/yyyy` and the
upstream `mm/dd/yyyy` alike. A first pass accepted only `mm/dd/yyyy` and `yyyy-mm-dd`, which flagged a column that
already had a perfectly good regional format with the sentence "should have a date format string", and offered a
one-click fix that replaced it. Because a flagged column has no format string, and `apply_fix` now refuses to
OVERWRITE a value on an object the rule does not currently flag (`LocalEngine.BpaFixAsync`; re-applying a fix that
already landed still writes the same value, so a repeat call is not a refusal), the fix can only ever fill an empty
format string. The description also said "Month" where the expression checks "Date"; it now says "Date". (Since round
9 on 28 Sep this fix never applies; see "THE FORMAT FILL RULE" below.)

**`MONTHCOLUMN_FORMATSTRING` was given the same treatment (27 Sep, round 3).** The date rule above was corrected and
its sibling one rule below was not, so the corpus shipped a promise and its own counter-example at the same time: on
the dogfood model a DateTime column named `MonthStart` carried `yyyy-mm-dd`, the exact literal the DATE rule's own fix
writes, and the MONTH rule flagged it and one press replaced it with `MMMM yyyy`. Upstream's expression is
`FormatString <> "MMMM yyyy"`, so every real date format fails it. It now flags only a month column with NO format
string at all, and the fix still writes `MMMM yyyy`, so like the date rule it can only ever fill an empty value. The
description was rewritten to say exactly that.

The same sweep was run across the whole file for other rules of that shape, and the result is recorded here so the
next reader does not repeat it. Only two rules are scoped at a column and carry a fix that writes `FormatString`, and
both are now empty-only: `DATECOLUMN_FORMATSTRING` and `MONTHCOLUMN_FORMATSTRING`. That pair is pinned by
`DogfoodFeedbackRulesTests.No_shipped_column_format_rule_replaces_a_format_string_that_is_already_there`, so a third
one cannot arrive unnoticed. Two more rules write `FormatString` from a fix, `PERCENTAGE_FORMATTING` and
`INTEGER_FORMATTING`, and both are scoped at Measure. `INTEGER_FORMATTING` carries the same hazard on that other
object kind (upstream's expression has no data-type guard at all, so a measure formatted `Long Date` is raised and its
fix would write `#,0`). Round 3 left it alone and recorded it here as known and unfixed, because the finding named
columns and because a guard on an upstream numeric rule moves finding counts on every model. Round 5 fixed it, and
the next section is that change.

**`INTEGER_FORMATTING` and `PERCENTAGE_FORMATTING` only tidy a format string they can prove is a number (28 Sep,
round 5).** The measure half of the hazard above is fixed here, so the paragraph above is history rather than a
standing limit. This section is itself history now: round 5's proof ("no Latin letter, no dollar sign") was replaced
in round 7 by the grammar in the next section, because it let every other currency and every non-Latin letter through. Upstream's `INTEGER_FORMATTING` expression is
`not FormatString.Contains("$") and not FormatString.Contains("%") and not (FormatString = "#,0" or FormatString = "#,0.0")`.
It has no data-type guard and no "only when empty" clause, so every real date and time format fails it: a measure
formatted `Long Date` was raised by a rule titled "Whole numbers should be formatted with thousands separators and no
decimals", and one press of its fix replaced the date format with `#,0`.

The two column rules answer that by firing only on an empty format string, because a date column's format was never
theirs to choose. A numeric house-style rule cannot borrow that answer, because tidying `0.00` into `#,0` is the whole
point of it. So the guard here is the other half of the same principle: the rule may only touch a format string it can
PROVE is a number. The proof is `not Regex.IsMatch(FormatString, "[A-Za-z]")` together with at least one `0` or `#`
placeholder. No letter anywhere rules out every named date and time format (`Long Date`, `Short Date`, `General Date`,
`Long Time`) and every custom one (`yyyy-mm-dd`, `dd/mm/yyyy hh:nn`, `mmm yyyy`); the placeholder rules out a string
that is neither a number nor a date. `PERCENTAGE_FORMATTING` gained the same two conditions on top of the `%` it
already required, so a percentage someone wrote words into, such as `0.0%" of plan"`, is no longer rewritten with the
words dropped. Each rule also gained a `Description`, which neither had, saying exactly what it will and will not
touch.

What this costs, honestly: fewer findings on real models, in three places. A scientific format such as `0.00E+00`
carries a letter, so it is left alone rather than flattened to `#,0`. A number format with a unit written into it,
such as `#,0 "days"`, is left alone for the same reason. A named numeric format with no placeholder, such as
`Standard` or `Percent`, was never proved to be a number, so neither rule touches it. All three are conservative
losses on purpose: leaving a deliberate format alone is the cheaper mistake, and a person can still set any format
they like with `set_property`. What did NOT change is the rule's real population. A measure with no format string at
all is still flagged, because filling an empty value replaces nothing, and a plain number format that is not the house
style, such as `0.00` or `#,##0`, is still flagged and still tidied by one press.

Tests: `DogfoodFeedbackRulesTests.The_integer_rule_leaves_a_real_date_or_time_format_alone` (7 cases),
`The_integer_rule_advises_a_plain_number_format_and_fills_only_an_empty_one` (named `The_integer_rule_still_tidies_...` until round 8),
`The_percentage_rule_only_advises_a_format_it_can_prove_is_a_percentage` (named `..._only_tidies_...` until round 8), and the whole-file guard
`No_shipped_format_rule_writes_over_a_format_string_with_a_letter_in_it` (4 cases, on a measure and a column at
once), plus the verifier's `VerifyFeedbackRulesRound5Tests` (7 cases), which is written against every shipped rule
whose fix writes `FormatString` rather than against either rule by name.

**Both measure format rules now prove what a format IS, not what it is not (28 Sep, round 7).** Round 5's proof ruled
things out: no letter in `[A-Za-z]`, no `$`. The verifier's round-6 report found three holes in it, each measured on
both doors. A measure whose format is a **format string expression** has an empty static format string, so the empty
branch raised it and one press wrote `#,0` beside the dynamic format (D1). Every **currency** but the dollar passed, so
`€ #,0.00`, `£ #,0`, `¥ #,0`, `₹#,0.00` and the rest were raised and one press dropped the symbol and the decimals (D2).
And a unit or a word in any **script but Latin**, such as `#,0 "шт"` or `0.0% "от плана"`, was raised and dropped (D3).
Patching each hole (a currency class, a Unicode letter class) would keep the same shape of proof, which fails open on
the next thing nobody listed. So the coordinator's ruling was to turn it round, and both rules now fire only on:

- **no format at all**, meaning an empty static format string AND no format string expression
  (`string.IsNullOrWhitespace(FormatStringExpression)`, the same test `PROVIDE_FORMAT_STRING_FOR_MEASURES` uses), and
  only `INTEGER_FORMATTING` takes this branch; or
- **a plain-number format string**, again with no format string expression: every character is one of `0 # , . ; ( )`,
  space, `-` or `%`, except for quoted pieces holding only that punctuation (such as `"-"` for a zero section). The
  rule expresses it as `Regex.Replace(FormatString, "[0#,.;() %-]|""[,.;() -]*""", "") = ""`: remove every allowed
  token and nothing may be left. A quoted digit, `#` or `%` is text, not a placeholder or a percent, so it is not an
  allowed token: `0"%"` shows a percent sign without scaling the number, and the percentage house style would have
  multiplied it by a hundred. The string still needs one `0` or `#`, `INTEGER_FORMATTING` still needs no `%`, and
  neither may already be its own house style.

Anything else is left alone, whatever script, currency or unit it carries, static or dynamic, and because `apply_fix`
refuses to overwrite a value on an object the rule does not flag, the one-click fix and Fix all cannot write over it.
Both Descriptions were rewritten to say exactly this. The `$` exclusion upstream carries is now implied by the grammar,
so it was dropped rather than kept as a second, weaker proof.

What this costs, on top of round 5's list: a number format written with a backslash escape (`\-#,0`) or with a
per-mille sign is left alone, and so is a stale static format on a measure that also has a format string expression.
What did NOT change is every plain number and percentage the verifier's probe P2 listed: each is still raised by the
right rule alone and tidied by one press, which is pinned case by case.

Tests: `DogfoodFeedbackRulesTests.No_measure_format_rule_touches_a_format_outside_the_plain_number_grammar` (18 cases,
including `hh:nn:ss.00` and `mm:ss.0`, time formats with a `0` in them that the placeholder check alone cannot keep
safe, which closes the gap where round 5's letter guard could be deleted with no test failing),
`A_measure_with_a_dynamic_format_is_left_alone_even_beside_a_stale_static_one` (2),
`Both_measure_format_rules_still_advise_every_plain_number_and_percentage` (28, the P2 list and both house styles),
`The_flag_rule_never_raises_a_date_column_for_its_name`, `The_format_rule_descriptions_say_what_the_expressions_do`,
and the verifier's `VerifyFeedbackRulesRound6Tests` (18 cases), written against every rule whose fix writes
`FormatString`.

This section is history too. Its sentence "because `apply_fix` refuses to overwrite a value on an object the rule does
not flag, the one-click fix and Fix all cannot write over it" was incomplete: Fix all was safe because it scans again
on every sweep, and the change-plan door, a third writer, was not covered at all. Round 8 replaces it.

**No fix writes over a format string that is already there (28 Sep, round 8).** The verifier's round-8 report found
that round 7's grammar is a list of allowed characters, not a grammar of a plain number. Three kinds of format built
only from those characters were raised, and one press wrote the house style over them: a number divided by a thousand
(`##0,,` shows 100 million as 100), a section that hides values (`0.00;;` shows nothing for a negative value or zero),
and a digit mask (`0000-00-00` lays out a date key). And the change-plan door applied a fix it had seeded before the
person gave the measure a format string expression, so `#,0` landed beside a dynamic format. Eight rounds each found a
format a grammar did not know, so the coordinator's ruling ends the class instead of patching the grammar again:
**a fix only ever fills a blank format string on an object with no format string expression. It never writes over
one, on any door.**

That rule is enforced once, in the engine, not in this file. `BpaAnalyzer.CanAutoFix(rule, obj)` answers false for a
fix that targets `FormatString` when the object's format string is not blank or the object has a format string
expression, and `BpaAnalyzer.ApplyFix` refuses the same write in words. Every door asks the first before it offers,
seeds or sweeps a fix, and every write goes through the second: the Fix button and `apply_fix`, Fix all and
`apply_safe_fixes`, and plan seeding. The change-plan door now also asks the rule again when it applies a `bpa_fix`
item, the way `apply_fix` already did, and skips the item with a plain note when the rule no longer raises the object
or the fix can no longer apply there; a fix that has already landed is a quiet no-op there, as it is for `apply_fix`.
The plan's own format items, `set_measure_format` (seeded from the readiness finding `FMT-MEASURE` and written by the
assistant) and `set_format_string`, are skipped the same way when the measure has been given a format string
expression since the plan was built. The readiness safe fixes write no format string at all. The rule holds for a
custom rule too, because it lives in the analyzer and not in any one rule, and it covers a fix that targets
`FormatStringExpression` as well: such a fix may fill a measure with no format, but never replaces a dynamic format or
sets one beside a static format. `get_fix_prompt` calls a withheld format fix advice: it tells the assistant what the
current format is, what the rule suggests, and to change it only if the person agrees.

In this file, both measure rules keep round 7's expression, so what they RAISE is unchanged. What changed:

- `INTEGER_FORMATTING` keeps its fix, `#,0`, which now only ever fills a blank format. A plain-number format that is
  already there, such as `0.00`, is advice with no one-click fix.
- `PERCENTAGE_FORMATTING`'s fix never applies. Every measure it raises already has a `%` in its format string, so its
  fix could never be the fill of a blank one: no door offers it, and a press on it is refused in words. The
  `FixExpression` stays, for two reasons. The refusal names the rule's house style, `#,0.0%;-#,0.0%;#,0.0%`, which the
  Description also states. And every test written against "every shipped rule whose fix writes `FormatString`" (the
  verifier's rounds 5, 6 and 8) keeps covering this rule, instead of passing without looking at it.
- Both rules moved from severity 2 to severity 1. The deploy gate blocks on a severity-2 finding that has no automatic
  fix, so at severity 2 this advice would have blocked publishing any model with a `0.0%` or `0.00` measure. Before this
  round neither rule ever blocked, because its fix was always offered; at severity 1 neither blocks now.
- Both Descriptions say exactly this. They say "any number of quoted pieces", not "at most a quoted piece", because the
  expression removes every quoted run, and "blank (empty, or only spaces or other blank characters)", not "no format
  string at all", because the expression is `FormatString.Trim() = ""`.

What this costs, honestly: one press no longer tidies `0.00` into `#,0` or `0%` into the house style. A person or their
assistant makes that edit on purpose, with `set_measure_format` or `set_property`, and the finding says what it suggests.

Tests: the verifier's `VerifyFeedbackRulesRound8Tests` (17 cases; its helper fails on a finding that OFFERS a fix, as
the ruling allows advice), `DogfoodFeedbackRulesTests.No_fix_writes_over_a_format_string_on_any_door`, `The_format_advice_never_blocks_the_deploy_gate`
(the gate's blocking count, measured), the plan cases `A_planned_format_fix_is_skipped_when_the_rule_still_raises_a_measure_that_now_has_a_dynamic_format`,
`A_planned_measure_format_is_skipped_when_the_measure_now_has_a_dynamic_format` and `A_planned_fix_that_already_landed_still_reads_as_applied`,
`A_fix_that_sets_a_format_string_expression_is_never_automatic` (named `..._only_fills_a_measure_with_no_format` until round 9), and the P2
control `Both_measure_format_rules_still_advise_every_plain_number_and_percentage`, which now pins advice with no fix.

**THE FORMAT FILL RULE (coordinator, 28 Sep, round 9).** Round 8 still filled every BLANK format. The plan-rows
verifier found that a blank fill can change what a person reads as badly as a write over a format: `#,0` over
`UTIL_PCT = DIVIDE ( [HRS_BILL], [HRS_TOT] )` shows 73% as 1, over a SUM of money it drops the cents, over hours it drops
the fractions, and over a MAX of a date it shows a serial number; `yyyy-mm-dd` over a date column hides the time a date
holds. The coordinator's rule narrows round 8's: **an automatic format fill happens only where the engine can PROVE the
value is a whole number.** A count (COUNT, COUNTA, COUNTROWS, DISTINCTCOUNT, DISTINCTCOUNTNOBLANK), or a SUM of a
whole-number column that is not a key, an ID, a date key, a code or a flag, may be filled with `#,0`, and so may such an
Int64 column. (Round V1f narrowed the proof further: see below. MIN and MAX are no longer proved. The M11 join narrowed
the column itself once more: a column's own fill also needs proof the model sums it, a SUM measure that reads it or a
summarize set to Sum, because a store or terminal number left at the default summarize has the same structure as a
quantity; `FormatFill.IsProvedSummed`.) Nothing else is filled automatically: not a ratio, not a decimal or currency sum, not a date, not a
formula the engine cannot classify, and never an object with a format string expression.

Like round 8's rule, it is enforced once, in the analyzer, not in this file: `FormatFill.MayFill` is the single "may
fill" check, and `BpaAnalyzer.CanAutoFix` and `BpaAnalyzer.ApplyFix` ask it for every fix that targets `FormatString`,
so every door (the Fix button and `apply_fix`, Fix all and `apply_safe_fixes`, plan seeding and the plan's re-check)
holds it, built-in rule or custom. A fix that targets any other format property (a measure's or calculation item's
`FormatStringExpression`, a calculation group's `NoSelectionFormatStringExpression` and
`MultipleOrEmptySelectionFormatStringExpression`, a KPI's `TargetFormatString`) is never automatic, because what an
expression shows cannot be proved. Every finding whose fix is withheld is advice (`BpaViolation.Advice`): the deploy gate
never blocks on it, the Studio gives it to the assistant as "Review the advice" rather than "Fix", and `get_fix_prompt`
and the press's refusal say the object's own format (its expression when it has one), what the rule suggests, why no
fix makes it, and a write that fits the object.

**Round V1f (coordinator addendum, 29 Sep).** Seven gaps in the proof closed, all in `FormatFill` and the callers, none in the
rule expressions. Jev's format lines (measures and columns) now ask `FormatFill.MayFill` like every other automatic path: a line it
proves may arrive approved, any other arrives as a suggestion the person keeps, and Run the plan never applies one. A calculation
group item or expression with no format string expression of its own stops every measure fill, because it would show the count
through its own arithmetic. MIN and MAX of an integer column are never proved (years, keys and codes live there), and a SUM of an
Int64 column, or its own format, is proved only by what the model says (not a key, not unique, no data category, on neither side of
a relationship, not a sort key or a hierarchy level, summarised by sum): a name may only veto, never prove. A `//` or `--` comment
ends at every line break DAX reads (`\n`, `\r`, U+0085, U+2028, U+2029). A planned fill that no longer holds says why in true words.
The deploy gate never blocks on `PROVIDE_FORMAT_STRING_FOR_MEASURES`, which is advice, and a format made only of blank spaces counts as
blank for the advice but is never written over. The `INTEGER_FORMATTING` Description says the same, and its `bodySha256` in
`third-party-manifest.json` moved with it. `Semanticus.Tests/DogfoodFormatFillV1fTests.cs` pins each item.

In this file only three Descriptions changed, so they say what the engine does:

- `INTEGER_FORMATTING`: its one-click fix fills in `#,0` only when Semanticus can prove the measure holds a whole
  number; any other blank measure is advice. Its expression, severity and fix are unchanged.
- `DATECOLUMN_FORMATSTRING` and `MONTHCOLUMN_FORMATSTRING`: advice only, with no one-click fix; each still names the
  format it suggests (`yyyy-mm-dd`, `MMMM yyyy`). Their expressions and severity are unchanged, and each keeps its
  `FixExpression`, for the reason `PERCENTAGE_FORMATTING` keeps its own: the refusal and the advice name the suggestion
  from it, and the tests written against "every shipped rule whose fix writes `FormatString`" keep covering them.

What this costs, honestly: one press no longer fills a blank decimal, currency, ratio or date format. The person or
their assistant chooses the format, and the finding says what the rule suggests and why it did not write it.

Tests: the verifier's `VerifyFeedbackRulesRound9Tests` (21 cases) and `verify-format-advice-task.test.mjs`;
`DogfoodFeedbackRulesTests.Format_fill_may_fill_only_a_proven_whole_number`,
`Format_fill_may_fill_only_an_int64_column_that_is_a_quantity` and `The_raw_csv_model_fills_only_the_counts_on_every_door`;
the tests that pinned the old fills now pin advice or use a count (`The_date_rule_advises_the_iso_form_and_accepts_it`,
named `The_date_format_fix_writes_iso_dates_and_the_rule_accepts_them` until round 9;
`The_month_rule_leaves_a_real_date_format_alone_and_advises_on_an_empty_one`, named `..._and_still_fills_an_empty_one`;
`The_integer_rule_advises_a_plain_number_format_and_fills_only_an_empty_count`, named `..._fills_only_an_empty_one`).
**Three rules stop fighting the product and the readiness rules (28 Sep, the Plan statement check).** An assistant
took a raw model to readiness grade A using only Semanticus tools, and three BPA findings on the way could not be
cleared without fighting what the product had built or what another rule had asked for (the check's "Defects" item 7).

`REDUCE_USAGE_OF_CALCULATED_TABLES` no longer raises a generated calendar. Upstream's expression is `1=1`: every
calculated table fails, including the `CALENDAR` date table Semanticus itself builds from a model spec (the SQL draft and
"Start from scratch" both make one and mark it). A model built by the product could therefore never be clean without a
waiver. The expression now clears a calculated table whose whole expression is `CALENDAR(...)` or `CALENDARAUTO(...)`, or
`ADDCOLUMNS` over one of them, in any case and spacing:
`Expression = null or not Regex.IsMatch(Expression, "(?i)^\s*(ADDCOLUMNS\s*\(\s*)?CALENDAR(AUTO)?\s*\(")`. That is a date
table built the way Microsoft's own date table guidance lists as a valid technique, and "move it to the warehouse" is not
advice anyone can act on for it. Every other calculated table is still raised, including one that uses a calendar inside
other logic (`FILTER(CROSSJOIN(..., CALENDARAUTO()), ...)`). What this costs, honestly: an `ADDCOLUMNS` over a calendar is
spared whatever columns it adds, and a calendar whose expression starts with a comment is still raised.

`INTEGER_FORMATTING` no longer raises `#,##0` or `#,##0.0`. After round 7 it raised every plain number format that
was not exactly `#,0` or `#,0.0`, so `#,##0` (the most common way to write the same house style) was raised, and one
press of Fix all rewrote `#,##0.0` on an hours measure to `#,0`, dropping the decimal the assistant had chosen. Both
formats already show thousands separators, which is what the rule asks for, so they now join `#,0` and `#,0.0` as the
formats the rule treats as done: the last clause of the round-7 expression reads
`and not (FormatString = "#,0" or FormatString = "#,0.0" or FormatString = "#,##0" or FormatString = "#,##0.0")`, and the
description says so. That is the whole change. Every other format round 7 raised is still raised, and how the rule's fix
writes is untouched: the separate fix on the m11/feedback-rules lane (a fix only fills a blank format and never writes
over one) governs every write at merge. A first version of this change narrowed the rule to empty and bare whole-number
formats only; it was taken back in the second round because it changed what the rule raises well beyond the two
formats the check met, and it collided with that lane's fix. `PERCENTAGE_FORMATTING` is unchanged.

`UNNECESSARY_COLUMNS` never raises a key column. Upstream raises any hidden column nothing references, and BPA then
contradicted itself and the product. `MODEL_SHOULD_HAVE_A_DATE_TABLE` and
`DATE/CALENDAR_TABLES_SHOULD_BE_MARKED_AS_A_DATE_TABLE` both need a DateTime `IsKey` column on the marked date table, so
a date table marked on a hidden `[Date]` while its relationship runs on an integer `DateKey` (the common warehouse shape)
was told to delete that column, and deleting it reopened both date findings. And "Draft from a SQL source" hides every
primary-key column it declares, but TOM holds `IsKey` on at most one column per table, so on a two-part key such as
`ref_codes(CODE_TYPE, CODE)` the other part was hidden, unreferenced and not `IsKey`, and the rule asked to delete half of
the key (a publish blocker on the product's own model; the code repeats across types, so the lookup turns ambiguous).
The model spec build now writes a `Semanticus_SourceKey` annotation on every column the spec marks as a key
(`Semanticus.Analysis/SourceKeys.cs`), and the expression ends `and not IsKey` then `and not
HasAnnotation("Semanticus_SourceKey")`, placed last so the evaluation order of every upstream clause is unchanged. A key
is the row identity the model declares, which reference counting cannot see. What this costs, honestly: a part of a
composite key on a model built outside Semanticus, or before this change, carries no annotation, and nothing in the
model can tell it from an unused column, so it is still raised there. Every other hidden, unused column is still raised,
audit and ETL columns included: nothing uses them, they cost memory, and removing them is the clean end state. The
readiness rule `VIS-TECH-COLUMN`, which asks for such a column to be hidden from Copilot, now says what this rule will do
next, and only when it is true: for a column this rule would raise once hidden it says removal comes next, and for a key
or a column something runs on (a relationship, a sort, a hierarchy, a formula, a security filter or role) it says to
keep the column and why. Below compatibility level 1400 this rule cannot evaluate at all (upstream's object-level
security clauses have nothing to read, so BPA reports it as unknown), and there the readiness text says the column can
be removed without naming BPA. The Plan statement check's own examples (a fact row id and an audit copy of the source row)
were never asked to be hidden by any readiness rule, and removing them is what this rule rightly asks; they are still
raised. The description now says what the rule spares.

Tests: `PlanStatementRuleClashTests` (the spec-built date table, 9 calculated-table shapes, a hidden date key and a hidden
non-date key, every part of a spec-declared two-part key spared beside a hidden unused column still raised, the
VIS-TECH-COLUMN text checked against this rule clause by clause in 8 shapes, `#,##0` and `#,##0.0` not raised, and 9
formats still raised or not as round 7 had them), and the verifier's `VerifyRuleClashesTests` (the SQL draft's own
two-part key, and the technical-column text on a relationship's key and on a table's key). In `DogfoodFeedbackRulesTests`
one row of the round-7 P2 list changed: `#,##0` is now raised by neither rule and left as written. The final batch's
`RuleClashesFinalFixTests` add a three-part key carried through "Autogenerate from model" on both doors (the spec
generator now reads the key mark, not TOM's single `IsKey`), and the `VIS-TECH-COLUMN` text under Tabular Editor's own
ignore annotation, where this rule lists the column as accepted and the text no longer calls removal its advice.

One limit, stated plainly. A model can carry its own copy of a rule: Tabular Editor's embedded rules, and any set
`load_rules` saves, live on the model's `BestPracticeAnalyzer` annotation and replace a bundled rule with the same id
(`GetBpaRules` in `LocalEngine.cs`). A model that carries an older `UNNECESSARY_COLUMNS` therefore raises key parts again,
and the `VIS-TECH-COLUMN` text still follows this bundled rule, not the model's copy. Those are the person's own rules, so
they are left in charge.

## Our portion — 5 of 74 rules

The rules whose IDs begin `SEM_` are Semanticus's own work: our wording, our expressions, and ours
to license. They replace checks the Microsoft corpus does not make.

| ID | Replaces the loss of |
|---|---|
| `SEM_COLUMN_NEEDS_FORMAT_STRING` | format strings on all visible numeric and date columns, not only ones named "Date" or "Month" |
| `SEM_RELATIONSHIP_KEY_NAMES_SHOULD_MATCH` | relationship key columns sharing a name (Microsoft checks the data type instead) |
| `SEM_UNFINISHED_MARKER_IN_EXPRESSION` | TODO and similar markers left in a shipped expression |
| `SEM_MEASURES_NEED_DISPLAY_FOLDERS` | display-folder organisation for measures |
| `SEM_COLUMNS_NEED_DISPLAY_FOLDERS` | display-folder organisation for columns and hierarchies |

Where one of these reuses an expression *shape*, it is composed from Microsoft's own MIT-licensed
rules in this same file. No text from the previously shipped corpus is carried across.

## What this replaced, and why

Until this change the file was byte-identical to the `TabularEditor/BestPracticeRules` repository,
which publishes no licence file at all, while our notices claimed MIT for it. That claim could not be
supported, so the corpus was replaced rather than re-labelled.

## Known coverage gap

Microsoft's corpus contains no culture or translation rules, and our separate AI-Readiness corpus
covers no translation ground either (it reads cultures only as the container for synonyms). So
this change removes our only checks for translated object names, descriptions, display folders,
hierarchy levels and perspective names. No customer-facing text claims that coverage, so nothing is
now false, but the gap is real and deliberate. It is recorded here rather than dropped silently.
