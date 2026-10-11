You are writing a blind, held-out test set for a product. You will not see the product's code or its rules, and you must not look for them: do not read any file outside this folder. Work only in this folder.

What the product does: it suggests and writes format strings on measures and columns of Power BI / Analysis Services tabular semantic models, some automatically, some after a person ticks; a model can change between the suggestion and the write.

The harm the set tests for: a number format written on a measure or column that makes the number it shows wrong or misleading: a year shown as 2,024, a rate or money shown as a whole number, a code with a thousands separator.

Your task: write 4 to 6 semantic models in TMDL, together at least 80 measures and columns, across real business shapes (sales, finance, HR, inventory, web analytics) and adversarial ones (years and codes as integers, ratios hidden behind other measures, calculation groups, date tables not marked, a measure that changes after it was ticked, and any other trap you think a product that writes formats would fall into). Label each measure and column with the formats that are RIGHT for it, the formats that are WRONG for it, or U (unsure). Also write a script of model changes to apply between the suggestion and the write.

Deliverables (write all files into this folder):

1. `models/<ModelName>/`, one per model: a TMDL folder in the standard layout that Microsoft's TMDL serializer (TmdlSerializer.DeserializeDatabaseFromFolder) loads: `database.tmdl`, `model.tmdl`, `relationships.tmdl` (when there are relationships), and `tables/<Table Name>.tmdl` (calculation group tables included). Compatibility level 1601 or higher. Indent with tabs. Every table needs a partition (an M `#table(...)` literal or a plausible source query is fine; no real connection strings). Give every column a correct `dataType`, and a `sourceColumn` (or an expression for a calculated column). Leave `formatString` off every measure and column you want the product to act on; if you deliberately give an object a format string already, record it in its label.

2. `labels.json`: one entry per measure and column in every model, with these fields:
   - `id`: "<Model>|<Table>|measure|<Name>" or "<Model>|<Table>|column|<Name>"
   - `what`: one line on what the number is (for example "fiscal year stored as an integer")
   - `shipped_format`: the format string it already carries in your TMDL, or null
   - `unsure`: true when you cannot say which formats are right or wrong for it (this is the U label), else false
   - `right`: example format strings that are RIGHT for it
   - `wrong`: example format strings that are WRONG for it
   - `rule_in_words`: one or two sentences that decide any other format string for it
   - `provable_from_model`: true when the right format can be worked out from the model alone (names, data types, expressions, relationships, table and column properties) without outside business knowledge; false otherwise
   - `why`: a short reason for the labels and for `provable_from_model`
   - `adversarial`: a short tag when the object is a trap, else null

3. `changes.json`: the script of model changes, applied after the product has suggested formats and before it writes them. Each entry: `id`, `model`, `when` ("before the person ticks" or "after the person ticks, before the write"), `target` (an id as in labels.json, or a new object), `change` in plain words, `tmdl_after` (the full TMDL of the changed object after the change, or "deleted"), and `labels_after`: the full label entries (same fields as labels.json) of every object whose right or wrong formats change because of it. Include at least 8 changes, covering at least: a measure whose expression changes after it was ticked so that the ticked format becomes wrong, a column whose data type changes, a rename, and a deletion.

4. `judge.py`: Python 3, standard library only. `python3 judge.py <written.json>` reads a JSON list of {"id": ..., "format": ..., "after_changes": true|false} and prints, for each, RIGHT, WRONG, U or UNKNOWN_ID, using your labels (and `labels_after` when after_changes is true and a change touched that object). It must decide every format string, including ones not in your example lists, by coding each object's `rule_in_words` (treat obvious equivalents alike, such as "#,0" and "#,##0"). Include a self-test (`python3 judge.py --self-test`) that checks every `right` and `wrong` example against the judge, and make it pass.

5. `README.md`: what each model is, which objects are traps and why, and anything you were unsure about.

Make the models realistic: names and shapes like real business models, including messy ones. Do not use real people's names.
