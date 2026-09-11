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
rule changed. The upstream version pin did not move.

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
covers no translation ground either (it reads cultures only as the container for Q&A synonyms). So
this change removes our only checks for translated object names, descriptions, display folders,
hierarchy levels and perspective names. No customer-facing text claims that coverage, so nothing is
now false, but the gap is real and deliberate. It is recorded here rather than dropped silently.
