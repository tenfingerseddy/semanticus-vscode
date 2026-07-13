# Third-Party Notices

Semanticus incorporates third-party material. This file preserves the required attributions. (The
Semanticus project's own license is recorded separately in the root `LICENSE` file once chosen; this
NOTICE is owed regardless of that choice and is reproduced in any binary distribution.)

---

## Tabular Editor 2 — TOMWrapper, UndoFramework, FormulaFixup, serialization helpers, DAX grammar

The engine compiles, in place, a subset of **Tabular Editor 2** (the `TOMWrapper` object model + undo
framework + `FormulaFixup`/dependency layer + serialization helpers + the ANTLR DAX lexer grammar),
vendored as a pinned git submodule at [`external/TabularEditor`](external/TabularEditor).

- Project: Tabular Editor — <https://github.com/TabularEditor/TabularEditor>
- License: **MIT**
- Copyright (c) 2025 Tabular Editor ApS

```
MIT License

Copyright (c) 2025 Tabular Editor ApS

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

> Note: the Tabular Editor tree also contains `FastColoredTextBox` (LGPLv3) and `TreeViewAdv` (BSD).
> **Neither is referenced or compiled by Semanticus** (the WinForms UI is replaced by a React webview),
> so no copyleft obligation attaches.

## Best Practice Analyzer rules — `BPARules-PowerBI.json`

The default BPA rule corpus (`Semanticus.Analysis/Rules/BPARules-PowerBI.json`) originates from the
Tabular Editor **BestPracticeRules** repository.

- Source: <https://github.com/TabularEditor/BestPracticeRules>
- License: **MIT** (Tabular Editor ApS)

## Dynamic LINQ (`DynamicLinq.cs`)

The BPA predicate evaluator (`System.Linq.Dynamic`) is the legacy Microsoft-provided `System.Linq.Dynamic`
sample, vendored through Tabular Editor 2. Microsoft sample code, used under its sample license.

## Microsoft Analysis Services client libraries (TOM / AMO / ADOMD)

`Microsoft.AnalysisServices.*` (TOM/AMO) and `Microsoft.AnalysisServices.AdomdClient` are referenced via
the public NuGet packages and ship under **Microsoft's proprietary Software License Terms (EULA)** — not an
open-source license. They are referenced (per Microsoft's guidance), not redistributed as source.

- <https://www.nuget.org/packages/Microsoft.AnalysisServices.Tabular/>
- AdomdClient EULA: <https://go.microsoft.com/fwlink/?linkid=852895>

## ANTLR runtime

`Antlr4.Runtime` / `Antlr4.CodeGenerator` (the DAX lexer codegen behind FormulaFixup) — **BSD-3-Clause**.

- <https://github.com/antlr/antlr4>

## Dax.Vpax / Dax.Model.Extractor / Dax.Metadata / Dax.ViewVpaExport (VertiPaq Analyzer)

`export_vpax` uses SQLBI's official **dax-tools** libraries (the engine behind VertiPaq Analyzer / DAX Studio)
to extract a DAX metadata model and write the `.vpax` interchange format. Referenced via the public NuGet
packages `Dax.Vpax`, `Dax.Model.Extractor`, `Dax.Metadata`, `Dax.ViewVpaExport`.

- Project: <https://github.com/sql-bi/Dax.Tools>
- License: **MIT** (SQLBI)

## Power Query / M tooling — `@microsoft/powerquery-{parser,formatter,language-services}` (bundled in the webview)

The Studio M Code tab's M editor (formatting, validity, autocomplete, hover-types) is powered by
Microsoft's browser-safe Power Query SDK libraries, bundled into the webview JS (`media/studio/studio.js`):

- `@microsoft/powerquery-parser`, `@microsoft/powerquery-formatter`, `@microsoft/powerquery-language-services`
- Project: <https://github.com/microsoft/powerquery-parser> (and the `microsoft/vscode-powerquery` family)
- License: **MIT** — Copyright (c) Microsoft Corporation

## Power Query M standard-library symbols — `Semanticus.VSCode/webview/src/mstdlib.ts`

The vendored M standard-library symbol dataset (866 built-in functions/constants/types — `Table.*`, `List.*`,
`Text.*`, `Date.*`, `Sql.Database`, …) that enriches M autocomplete + hover is copied verbatim from the
**vscode-powerquery** extension's `server/src/library/standard/standard-enUs.json` (pinned commit
`6a43c83b7ab8adc6e03bdb82c647030ce2ed041f`), re-embedded as a `JSON.parse` string module.

- Source: <https://github.com/microsoft/vscode-powerquery>
- License: **MIT** — Copyright (c) Microsoft Corporation
