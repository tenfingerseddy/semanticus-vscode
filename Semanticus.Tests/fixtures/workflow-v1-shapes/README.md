# v1 shape fixtures

Eleven v1 files carrying shapes **no shipped workflow uses**, so the v1-compatibility guard can see them. The
shipped corpus declares no version, carries no unknown key, no duplicate key, no comment-shaped value, no
comment-shaped line and no quoted comma in a list, which is how the version scan was changed under a guard
that could not observe it for three review rounds, and how two more v1 breaks then shipped past it.

The last three rows exist because that happened, not in case it might: PR #301's residual risk 6 said in
advance that this corpus proves only the shapes it contains, and the two class-A breaks of the fifth unscoped
read were both in shapes it did not contain. **The gaps that remain are named in the report for that read,
not hidden.** Adding a fixture is cheap; the expensive part is noticing a shape is missing, so record the
ones you rule out as well as the ones you add.

Their golden is `../../goldens/workflow-v1-shapes.txt`, captured by `../../tools/prev2-oracle/capture.ps1`
from the parser at the pinned pre-v2 commit. Never regenerate it from the parser under test.

## Every fixture names the assertion that consumes it, and the table is CHECKED

`WorkflowV1CompatibilityTests.Every_fixture_readme_row_is_true_of_the_code` reads this table and fails if the
rows and the `.md` files on disk are not the same set, if a row names no test at all, if a named test does
not exist, or if a named test does not CALL `Fixture("<stem>")`. Mentioning the file name in a comment does
not count: the test has to read the file.

**This proves the named test reads the fixture; it cannot prove the test asserts anything useful about what
it read.** No static check can, and that residual is a reason to review these tests on their merits, not a
reason to trust the table further than it goes.

That check exists because this table was itself the third instance of the failure it was written to prevent.
`slot-with-unknown-property.md` was written to carry a `schemaVersion:` inside a `slots:` block, and for one
commit nothing asserted the rule that shape exists to trigger: the golden compares parse output, the warn is
not parse output, and the test named here retyped the shape instead of reading the file. Sol proved it by
changing that line to `futureKey: 2`, at which point the fixture no longer carried its hazard and every named
test, including the golden, stayed green. The `comment-shaped-values.md` row was false the same way. Before
that, a round-3 control passed because its input lacked the hazard its name promised.

So the rule here is mechanical, not a resolution to be careful: **a row may only name a test that loads the
file.** If a claim cannot be pinned that way, delete the row rather than soften it.

What the check cannot prove is that a named test asserts anything USEFUL. Nothing mechanical can. It proves
the test would break if the fixture changed, which is precisely the property the two false rows lacked.

| Fixture | What it pins | Asserted by |
|---|---|---|
| `explicit-v1.md` | an explicit `schemaVersion: 1` parses as v1, is not warned about, **and lands in `Provenance`** as it did before v2 existed | golden; `R11_control_a_plain_v1_declaration_is_not_warned_about` |
| `duplicate-version.md` | two top-level `schemaVersion` lines: v1 loads, warns, and the caller-visible `Provenance["schemaVersion"]` is the LAST value (`2`), not the first | golden (`provenance schemaVersion=2`); `R11_a_v1_duplicate_version_keeps_the_LAST_value_in_provenance` |
| `slot-with-unknown-property.md` | a `slots:` block whose item carries properties v1 never defined stays open and inert, and the `schemaVersion` hidden in it is still reported | golden; `R12_a_version_hidden_inside_a_slots_block_is_still_reported` asserts the warn the golden cannot see |
| `unknown-top-level-key.md` | unknown top-level keys with values are preserved into `Provenance` (the v1 forward-compatibility promise) | golden; `R11_an_unknown_top_level_key_with_a_value_is_preserved` |
| `comment-shaped-values.md` | `key: # text` is a VALUE in v1, not a comment, on a top-level key **and** on a gate list key, where the value is dropped and the list beneath it is still read | golden; `R2_control_v1_reads_a_comment_shaped_value_exactly_as_it_always_did` |
| `quoted-comma-in-list.md` | v1 splits an inline list on EVERY comma and strips quotes afterwards, so `["finance, monthly"]` is two tags and `['a, b', plain]` is three triggers. A v1 defect, frozen | golden; `U5_v1_splits_an_inline_list_on_every_comma_including_a_quoted_one` |
| `comment-line-in-frontmatter.md` | a whole LINE shaped like a comment (`# owner: finance`, indented or not) is an ordinary key in v1 and its value is preserved into `Provenance` | golden; `U5_v1_reads_a_comment_shaped_frontmatter_LINE_as_an_ordinary_key` |
| `hash-and-apostrophe-in-list.md` | v1 cuts an inline comment at the first " #" even inside a quoted item, so the tag list truncates to the single literal tag `["finance`; and an apostrophe is stripped only from an item's ENDS, so `O'Reilly` survives whole | golden; `U6_v1_truncates_at_a_quoted_hash_and_keeps_an_apostrophe` |
| `comment-line-in-blocks.md` | inside a gate fence a column-zero `#` line ENDS the section above it, so the input listed after it is dropped in v1 | golden; `U5_a_column_zero_comment_shaped_line_ENDS_a_v1_gate_section` |
| `quoted-version-line.md` | a quoted LINE whose colon sits inside one closed pair of quotes (`"schemaVersion: 2"`, the single-quoted twin, and with trailing text) is a scalar, not a version key: v1 keeps each as provenance and loads. Read seven's class A; under the read-eight ruling the over-approximate warn fires for the shape, hedged | golden; `R13_a_quoted_LINE_that_merely_looks_like_a_version_key_stays_v1_forever` |
| `nonplain-version-keys.md` | the read-eight ruling: a QUOTED version key, an ESCAPED-quote key, and a block scalar whose content line starts with a quote all load exactly as the frozen reader loads them (provenance, `description=|`), stay v1, and draw the additive warn. The scan reads one spelling only; nothing classifies raw lines any more | golden; `R14_every_nonplain_version_key_spelling_loads_frozen_and_warns` |

These are test fixtures. They must never be installed as workflows, which is why they live under
`Semanticus.Tests` and not in any of the three shipped corpora.
