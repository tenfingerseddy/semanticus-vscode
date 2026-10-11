This folder holds a held-out test set you wrote earlier (models/, labels.json, changes.json, judge.py, README.md, build.py). Keep working only in this folder; do not read any file outside it.

The TMDL loader rejects every model, or reads one differently from what you meant. These are the loader's findings, and they are TMDL syntax only:

1. Every model fails on relationships.tmdl: "Parsing error type - InvalidName. There was an error parsing the object name - name = 'Sales[Order Date Key]', error=input could not be fully consumed! Line - 'fromColumn: Sales[Order Date Key]'". In TMDL a relationship names its columns as Table.Column, with any name that holds a space or another special character in single quotes, for example `fromColumn: Sales.'Order Date Key'` and `toColumn: Calendar.'Date Key'`.

2. After that, every model fails on the first column that carries a description: "Parsing error type - UnknownKeyword. Unsupported property - description is not a supported property in the current context! Line - 'description: YYYYMMDD join key'". TMDL has no `description:` property. A description is written as one or more `///` lines directly above the object's declaration line, at the same indentation as that line, for example:

	/// YYYYMMDD join key
	column 'Date Key'
		dataType: int64

3. With 1 and 2 corrected, FinanceLedger loads, but 'Time Calc' loads as an ordinary table, not a calculation group, because its partition is an M import partition. A calculation group table's partition is written `partition 'Time Calc' = calculationGroup` (no M source).

Fix these three syntax problems in the TMDL (fix build.py too if it generates the files, then regenerate). Change nothing else: keep every model, table, measure, column, expression, data type, description text, format string, relationship and label exactly as it is, and keep labels.json and changes.json byte-identical (the `tmdl_after` snippets are read by a person, not by the loader, so leave them as they are). Run `python3 judge.py --self-test` at the end. Say in your final message exactly which files changed.
