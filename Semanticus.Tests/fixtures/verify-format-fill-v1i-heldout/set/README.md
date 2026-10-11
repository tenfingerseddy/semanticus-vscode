# Held-out format-writing models

This set has five independent tabular databases and 144 labeled measures and columns. Each database uses compatibility level 1601, tab-indented TMDL, explicit column types and source columns, and import partitions backed by empty M `#table` literals. The empty data keeps the examples self-contained; names, DAX, relationships, and descriptions carry the intended semantics. The calendar tables are intentionally **not** marked as date tables.

| Model | Shape | Main format hazards |
| --- | --- | --- |
| `SalesOps` | Order lines, products, customers, calendar | Integer fiscal years, fixed-width promotion/product/postal codes, revenue-share and year-over-year ratios, ticked `Discount Rate` changing to dollars |
| `FinanceLedger` | Journal, account and department dimensions, fiscal calendar, `Time Calc` calculation group | Mixed-currency raw amounts, account/document/cost-center codes, exchange-rate precision, `Operating Margin` changing to dollars |
| `PeopleOps` | Employees, departments, absences, calendar | Integer birth years and employee numbers, fractional FTE and absence hours, a measure and a column renamed after suggestion |
| `StockFlow` | Warehouse snapshots, products, warehouses, calendar | SKU and batch codes, inventory money, stock ratios versus days of cover, deletion after a format was ticked |
| `WebPulse` | Event stream, campaigns, devices, calendar | Session years, conversion and bounce rates, x-multiple ad return, page-load source units changing from milliseconds to seconds |

`labels.json` has one entry for every original measure and column. `right` and `wrong` are examples; `rule_in_words` is the general policy. `U` is reserved for text fields and mixed-currency amounts where no single numeric format follows from the model. Some code widths and business precision requirements are supplied by the label rather than model metadata; those labels set `provable_from_model` to `false`. Descriptions in TMDL make many other units and precision requirements explicit, so those labels set it to `true`.

`changes.json` is ordered by phase: first changes before the person ticks, then changes after the tick and before the write. Apply each `change` to the named model. `tmdl_after` is the complete definition of the changed measure or column, or `deleted`. When a source schema changes, the accompanying plain-English `change` also specifies the partition edit. For renames, `new_id` gives the replacement identity; the old ID is gone. `labels_after` includes every object whose format semantics changed, including the dependent `Average Page Load` measure after its source column switches units. With `after_changes: true`, the judge uses the final state after all listed changes.

Run `python3 judge.py --self-test` to check every example in the baseline and final labels. To score output, pass a JSON list of `{ "id": "...", "format": "...", "after_changes": true }` objects to `python3 judge.py written.json`. It prints one tab-separated `id` and verdict per row: `RIGHT`, `WRONG`, `U`, or `UNKNOWN_ID`. A missing `after_changes` is treated as `false`. The judge checks all numeric sections of a custom format; grouping, decimal precision, units, percent scaling, and literal suffixes determine the verdict.

`build.py` regenerates the models, labels, and change script from one object inventory. It is included so the label-to-object coverage and generated TMDL are reviewable.
