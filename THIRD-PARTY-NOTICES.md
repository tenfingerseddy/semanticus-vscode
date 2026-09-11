# Third-Party Notices

Semanticus incorporates third-party material. This file preserves the required attributions. (The
Semanticus project's own license is the **Elastic License 2.0**, recorded in the root `LICENSE` file
and ratified in PR #149; this NOTICE is owed regardless of that license. Nothing below grants any right
under the Elastic License 2.0, and the Elastic License 2.0 grants no right in the third-party material
below.)

> **This file IS carried in the packaged `.vsix`, which closes the gap tracked as `[T189]`.** The extension
> is packaged from the `Semanticus.VSCode` directory, so `scripts/package.mjs` copies this file there before
> `vsce` runs, and `scripts/verify-vsix.mjs` REQUIRES `extension/THIRD-PARTY-NOTICES.md` in the payload.
> Deleting the staging step fails the packaging check by name rather than quietly shipping a `.vsix` with
> only the summary `Semanticus.VSCode/NOTICE` in it, which is what shipped before.

---

## Borrowing code

We copy permissively-licensed open-source code where the licence allows it, and we carry the credit.
That only stays safe if the paperwork is right every single time. Four steps, in this order.

**1. Verify the licence before you copy anything.** Read the upstream licence file at the exact commit or
release you are taking, and note how you read it. If the upstream project publishes no licence, it grants
you nothing: stop and escalate rather than copy. "Believed permissive" is not a verification and the gate
rejects it.

**2. Put the attribution in the file itself,** as a comment block at the very top, in the first 40 lines.
It has to name four things, because the file will be published on its own: the **upstream project**, the
**author or copyright holder**, the **licence**, and the **upstream URL**. Say what you changed, if
anything. Files that cannot carry a comment (JSON, generated bundles) are the only exception, and they need
a written `headerWaiverReason` in the manifest.

**3. Declare it in [`third-party-manifest.json`](third-party-manifest.json).** That file is the
authoritative record; this notices file is a view of it. An entry needs the path, kind, upstream, URL,
author, licence, **version** (a commit SHA or a release), the date you verified, and the evidence. A
licence without a version is not a record.

**4. Add it to this file** under its own `##` section, naming the tracked path, the licence, the URL and
the version, and reproducing the full licence text when the licence requires it (MIT does).

**Run the gate before you push:**

```
git submodule update --init --depth 1 external/TabularEditor   # FIRST: the gate FAILS without it, by design
cd Semanticus.VSCode && npm test                                # the whole extension battery, including this gate
node test/attribution-and-license.test.mjs                      # just this gate
```

The gate is `Semanticus.VSCode/test/attribution-and-license.test.mjs`.

**Exactly what it enforces.** For files and dependencies that are ALREADY DECLARED, it holds the manifest,
this file and the tree in agreement, in both directions: a declared file must exist, its body must still
match the hash recorded when its licence was verified, its header must name the upstream, author, licence
and permission notice, and this file must carry a section for it naming the path, licence, URL and version.
For the webview it compares the production dependency closure against `webview/package-lock.json` both ways,
so a new production dependency cannot arrive unrecorded. For the vendored Tabular Editor tree it hashes every
donor file against every tracked file and fails on an undeclared verbatim copy.

**Exactly what it does NOT do.** It does not prove that everything borrowed has been declared, and no check
here can. These gaps are real and are not covered:

- A borrowed file with its upstream attribution **removed**, from a project we do not vendor, leaves no
  signal at all. Nothing detects it.
- A borrowed **fragment** pasted into one of our existing files is not detected.
- Output from a borrowed **generator** is not detected.
- Prose derivation hints like "derived from" or "adapted from" are deliberately NOT gated: measured on this
  tree they match 49 files, essentially all of them ordinary commentary about our own code, and a 49-entry
  waiver list is one nobody reads.
- **NuGet is gated only for the references written LITERALLY in the project files of
  the three packaged projects.** `Semanticus.Core`, `Semanticus.Analysis` and `Semanticus.Engine` are scanned as TEXT for
  `PackageReference` and `ProjectReference` elements. Each package found must be a `nuget` entry in the
  manifest, with its notice in this file, or be classified in the gate with a written reason. The gate
  evaluates no MSBuild at all: it reads no reference out of an imported file, it expands no property
  beyond a single unconditioned definition in the same file, it evaluates no condition, and it proves
  nothing about which references a build copies into the payload. An asset setting such as
  `PrivateAssets` or `ExcludeAssets` therefore removes nothing on its own; it fails the gate unless a
  named exemption carries measured evidence for that exact package, version, project and setting.
  That population is NOT the resolved publish closure. Three things sit outside it and none of them is
  claimed anywhere in this file: the transitive packages those references pull in, the whole .NET 8
  runtime pack that the self-contained publish copies into the payload, and packages referenced only by
  test or smoke projects. **Deriving the resolved publish closure is the named open item on the board.**
- **A reference the gate cannot see fails it, rather than being missed.** MSBuild hands those projects
  `Directory.Build.props` and any file they import, and a `PackageReference` written there ships exactly
  like one in the project file while being invisible to a text scan. The gate reads every repo-controlled
  file those projects receive and fails by name if one declares a reference, or if an import cannot be
  resolved literally. That refusal is what keeps the bullet above true; it is not a claim that imports are
  audited.
- **The extension-root npm closure is ungated.** Only `Semanticus.VSCode/webview/package-lock.json` is
  checked, because that is what the tracked webview bundle is built from. `Semanticus.VSCode/package-lock.json`
  has no gate.
- The donor-copy hash only catches an **exact text copy** of a vendored Tabular Editor file of 512 bytes or
  more. Any edit, and any non-vendored donor, is outside it.
- Where a licence grant is recorded, the gate **checks the shape of the record, not its truth**. It forces a
  named human to attach a quotable artifact. It cannot tell a real grant from an invented one.
- The packaged `.vsix` now carries this file, staged by `scripts/package.mjs` and required by
  `scripts/verify-vsix.mjs`. What the gate proves is that the entry is present and that
  `Semanticus.VSCode/NOTICE` points at it by name. It does not read the packaged bytes back out of a built
  `.vsix`, so a packaging change that staged a stale or truncated copy would still pass here; the payload
  verifier that runs on a real `.vsix` is what covers that.

So step 1 and step 2 above are the real protection. **A green run is not evidence that nothing was borrowed
silently**, and it must not be cited as if it were.

**Working only on the .NET side?** The gate lives in the extension battery on purpose, so there is one set of
rules rather than two that drift. **A new `PackageReference` in a shipped engine project is covered:** the
gate fails until the package is a `nuget` manifest entry with a notice here, or is classified with a
reason. A new `PackageReference` in a test or smoke project is still not covered. What the gate also
covers on the .NET side is the declared copies under `Semanticus.Core` and
`Semanticus.Analysis`, and any file you add that carries someone else's copyright line. Run it with `node`;
initialise the donor submodule first or the donor-copy scan fails by design rather than skipping:

```
git submodule update --init --depth 1 external/TabularEditor
node Semanticus.VSCode/test/attribution-and-license.test.mjs
```

No `npm install` is needed; the gate imports only Node builtins. CI runs it on both operating-system legs.

**If you CHANGE the gate, run the mutation battery too:** `node tools/attribution/mutation-battery.mjs`. It
breaks the manifest, the lockfile, a source file and this notices file itself, each in a specific way, and
requires each one to fail the **exact assertion** meant to catch it, not merely some assertion in the same
check. The battery is the home of its own case count: it declares every case by label and prints the total it
actually ran as `mutations=N unproved=0` on the last line, so read the number there rather than from a figure
written here that can go stale. That distinction is not
pedantry: a licence rule once sat behind a bookkeeping assertion that always fired first, so the licence rule
never ran and the battery still reported success. It restores every file it touches and exits non-zero if any
mutation goes unproved.

---

## Tabular Editor 2 — TOMWrapper, UndoFramework, FormulaFixup, serialization helpers, DAX grammar

The engine compiles, in place, a subset of **Tabular Editor 2** (the `TOMWrapper` object model + undo
framework + `FormulaFixup`/dependency layer + serialization helpers + the ANTLR DAX lexer grammar),
vendored as a pinned git submodule at [`external/TabularEditor`](external/TabularEditor).

- Project: Tabular Editor — <https://github.com/TabularEditor/TabularEditor>
- License: **MIT**
- Copyright (c) 2025 Tabular Editor ApS

**One Tabular Editor 2 file is COPIED into this repository, not merely compiled in place**, and is
therefore published as part of the Semanticus source tree:

| This repository | Upstream path | Relationship |
|---|---|---|
| `Semanticus.Core/Grammars/DAXLexer.g4` | `AntlrGrammars/DAXLexer.g4` | Copy. Grammar rules unmodified; an MIT attribution header was added. Taken from submodule pin `7029129aa3f45d35f987d8f6ac7e5a971f28771c`, upstream blob `63d27cccf2ba9ac26a4e8b87418609614d471faa`. |
| `Semanticus.Core/Compat/SplitModelSerializer.LinuxPaths.cs` | `TOMWrapper/TOMWrapper/Serialization/SplitModelSerializer.cs` | Copy. MIT. Version `7029129aa3f45d35f987d8f6ac7e5a971f28771c`. URL https://github.com/TabularEditor/TabularEditor. Path joins use Path.Combine so a Tabular Editor folder opens and saves on Linux. |

The copy exists because `Semanticus.Core` runs `Antlr4.CodeGenerator` over a grammar inside its own
project directory. It is used under the MIT license reproduced below, not under the Elastic License 2.0.
The attribution gate named in the "Borrowing code" section above fails if this table, that file's header, or
any other undeclared verbatim copy of a vendored donor file drifts.

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

The default BPA rule corpus (`Semanticus.Analysis/Rules/BPARules-PowerBI.json`) is taken from Microsoft's
**Analysis-Services** repository. It is an `EmbeddedResource` in `Semanticus.Analysis.csproj`, so it is
redistributed in every build and published in the source mirror.

- Source: <https://github.com/microsoft/Analysis-Services>, path `BestPracticeRules/BPARules.json`
- Version: **50e8ce5028dd7046e73974ec69cb79af225e41b0**
- License: **MIT**, `Copyright (c) 2016 Microsoft`

The licence was read at that pinned commit rather than inferred: `LICENSE` at the repository root is the
MIT licence, and its full permission notice is quoted verbatim in three places, because each reaches a
different recipient: the rule file's own `//` attribution header, which is compiled into
`Semanticus.Analysis.dll` and so travels with the binary; `BPARules-PowerBI.PROVENANCE.md` beside it in the
source tree; and the extension's own `NOTICE` file, packaged as `extension/NOTICE`, which is the summary
notice a `.vsix` recipient receives. This file ships beside it in the package, so the summary notice is no
longer the only one that reaches a recipient.

Our copy is a **modified subset**, not a verbatim copy: 69 of the upstream 71 rules ship, two of those with
their scope narrowed, five Semanticus-authored `SEM_` rules are appended, documentation links were
stripped from 29 description fields, and the `PERCENTAGE_FORMATTING` FixExpression was rewritten so its
JSON string contains real semicolon characters instead of the six-character `\u003B` sequence the
upstream file stores. Every difference, with its reason, is enumerated in the `PROVENANCE`
file next to the rule file. MIT permits modification; the changes are recorded so the diff against upstream
is never a mystery.

> **What this replaced.** Until 2026-07-30 this corpus came from `TabularEditor/BestPracticeRules`, which
> publishes no licence of any kind: `LICENSE` and `LICENSE.md` return HTTP 404 on both `master` and `main`,
> the GitHub repository API returns `license: null`, and the README states no terms. An earlier "MIT" claim
> here was unsupported and was corrected to UNVERIFIED on 2026-07-29. Rather than relabel it, the corpus was
> replaced with Microsoft's MIT-licensed rules, ratified by Kane on 2026-07-30 under `[T186-K]`. That
> unlicensed corpus no longer ships and must not be reintroduced.

## Dynamic LINQ (`DynamicLinq.cs`)

The BPA predicate evaluator (`System.Linq.Dynamic`) is the legacy Microsoft-provided `System.Linq.Dynamic`
sample, vendored through Tabular Editor 2. Microsoft sample code, used under its sample license.

## Microsoft Analysis Services client libraries (TOM / AMO / ADOMD)

`Microsoft.AnalysisServices` (TOM/AMO) and `Microsoft.AnalysisServices.AdomdClient` are referenced via the
public NuGet packages and ship under **Microsoft's proprietary Software License Terms (EULA)**, not an
open-source license. They are referenced (per Microsoft's guidance), not redistributed as source.

**The two packages point at different EULA documents, so each one is named with its own link and its own
version.** This section previously carried only `linkid=852895`, which is AdomdClient's link, and let it
stand for both. It does not: the link below for each package was read from the `<licenseUrl>` element of
that package's own `.nuspec` inside the cached package at the exact version that ships.

- `Microsoft.AnalysisServices` **19.114.0** EULA: <https://go.microsoft.com/fwlink/?linkid=852989>
  (read from `<licenseUrl>` in `microsoft.analysisservices.nuspec`)
- `Microsoft.AnalysisServices.AdomdClient` **19.114.0** EULA: <https://go.microsoft.com/fwlink/?linkid=852895>
  (read from `<licenseUrl>` in `microsoft.analysisservices.adomdclient.nuspec`)
- Package listing: <https://www.nuget.org/packages/Microsoft.AnalysisServices.Tabular/>

## ANTLR runtime

`Antlr4.Runtime` 4.6.6 is the C# parser runtime used by the DAX lexer behind FormulaFixup. It ships
as a NuGet binary in the engine payload (`Semanticus.Core`). `Antlr4.CodeGenerator` 4.6.6 is referenced
with `PrivateAssets=all` and does not ship.

- Path: `Antlr4.Runtime`
- License: **BSD-3-Clause**
- Author: Sam Harwell, Terence Parr
- Version: **4.6.6**
- Source: <https://github.com/tunnelvisionlabs/antlr4cs>

The licence was read at git tag `v4.6.6` of `tunnelvisionlabs/antlr4cs`, which is the source
repository named on the `Antlr4.Runtime` 4.6.6 NuGet page. Reproduced verbatim:

```
[The "BSD license"]
Copyright (c) 2013 Sam Harwell
Copyright (c) 2013 Terence Parr
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

1. Redistributions of source code must retain the above copyright
   notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright
   notice, this list of conditions and the following disclaimer in the
   documentation and/or other materials provided with the distribution.
3. Neither the name of the copyright holder nor the names of its
   contributors may be used to endorse or promote products derived from
   this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE AUTHOR ``AS IS'' AND ANY EXPRESS OR
IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED.
IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY DIRECT, INDIRECT,
INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF
THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

## YamlDotNet

`YamlDotNet` reads the YAML interiors of format-v2 workflow files (`WorkflowParser.cs`; the frozen v1
ruleset never touches it). Referenced via the public NuGet package, pinned at **18.1.0** — **MIT**,
verified 2026-08-01 against the NuGet registry (`license type="expression">MIT`) and against
`LICENSE.txt` at the package's own pinned repository commit `748334a8fa7c227740018b284b71ad95cc6b7fc7`.
The package's net8.0 dependency group is empty, so it brings no transitive dependencies. It is a binary
package reference, not a vendored copy, so its `third-party-manifest.json` entry is declared with kind
`nuget` and names the package id where a vendored copy would name a tracked path. The MIT permission
notice is reproduced here:

> Copyright (c) 2008, 2009, 2010, 2011, 2012, 2013, 2014 Antoine Aubry and contributors
>
> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and
> associated documentation files (the "Software"), to deal in the Software without restriction, including
> without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to
> the following conditions: The above copyright notice and this permission notice shall be included in
> all copies or substantial portions of the Software. THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY
> OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
> HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
> OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.

- Project: <https://github.com/aaubry/YamlDotNet>

## Dax.Vpax / Dax.Model.Extractor / Dax.Metadata / Dax.ViewVpaExport (VertiPaq Analyzer)

`export_vpax` uses SQLBI's official VertiPaq Analyzer libraries (the engine behind VertiPaq Analyzer
and DAX Studio) to extract a DAX metadata model and write the `.vpax` interchange format. The engine
references the public NuGet packages `Dax.Vpax` 1.12.1 and `Dax.Model.Extractor` 1.12.0. Those
packages pull `Dax.Metadata` and `Dax.ViewVpaExport` as transitives, so those two also ship.

- Path: `Dax.Vpax`
- Path: `Dax.Model.Extractor`
- License: **MIT**
- Author: SQLBI
- Version: **1.12.1** (`Dax.Vpax`)
- Version: **1.12.0** (`Dax.Model.Extractor`)
- Source: <https://github.com/sql-bi/VertiPaq-Analyzer>

The licence was read from `LICENSE.md` on `sql-bi/VertiPaq-Analyzer`, the repository named on the
`Dax.Vpax` 1.12.1 and `Dax.Model.Extractor` 1.12.0 NuGet pages. Both package versions record MIT
on nuget.org. Reproduced verbatim:

```
MIT License

Copyright (c) 2019 SQLBI

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

## Newtonsoft.Json

`Newtonsoft.Json` 13.0.3 is referenced by `Semanticus.Core` and `Semanticus.Engine` and ships as a
NuGet binary in the engine payload.

- Path: `Newtonsoft.Json`
- License: **MIT**
- Author: James Newton-King
- Version: **13.0.3**
- Source: <https://github.com/JamesNK/Newtonsoft.Json>

The licence was read at git tag `13.0.3` of `JamesNK/Newtonsoft.Json`. Reproduced verbatim:

```
The MIT License (MIT)

Copyright (c) 2007 James Newton-King

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## Model Context Protocol C# SDK

`ModelContextProtocol` 1.4.1 is the official MCP C# SDK. `Semanticus.Engine` references it to serve the
MCP door, and it ships as a NuGet binary in the engine payload. The package pulls
`ModelContextProtocol.Core` 1.4.1 as a transitive from the same repository and the same pinned commit, so
that assembly ships under this same section.

- Path: `ModelContextProtocol`
- License: **Apache-2.0**
- Author: Model Context Protocol a Series of LF Projects, LLC
- Version: **1.4.1**
- Source: <https://github.com/modelcontextprotocol/csharp-sdk>

Copyright line, read verbatim from the `<copyright>` field of `modelcontextprotocol.nuspec` inside the
cached 1.4.1 package:

```
© Model Context Protocol a Series of LF Projects, LLC.
```

The shipped nuspec declares `<license type="expression">Apache-2.0</license>`. A link is not a notice, so the
full licence is reproduced below rather than pointed at. Apache-2.0 is an invariant document, so this
copy is the standard text and nothing about it is specific to this package; the canonical copy is at
<https://www.apache.org/licenses/LICENSE-2.0>. The gate pins the whitespace-normalized SHA-256 of the
text below, so a truncated or edited licence body fails instead of passing on a matching first line.

```
                                 Apache License
                           Version 2.0, January 2004
                        http://www.apache.org/licenses/

   TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION

   1. Definitions.

      "License" shall mean the terms and conditions for use, reproduction,
      and distribution as defined by Sections 1 through 9 of this document.

      "Licensor" shall mean the copyright owner or entity authorized by
      the copyright owner that is granting the License.

      "Legal Entity" shall mean the union of the acting entity and all
      other entities that control, are controlled by, or are under common
      control with that entity. For the purposes of this definition,
      "control" means (i) the power, direct or indirect, to cause the
      direction or management of such entity, whether by contract or
      otherwise, or (ii) ownership of fifty percent (50%) or more of the
      outstanding shares, or (iii) beneficial ownership of such entity.

      "You" (or "Your") shall mean an individual or Legal Entity
      exercising permissions granted by this License.

      "Source" form shall mean the preferred form for making modifications,
      including but not limited to software source code, documentation
      source, and configuration files.

      "Object" form shall mean any form resulting from mechanical
      transformation or translation of a Source form, including but
      not limited to compiled object code, generated documentation,
      and conversions to other media types.

      "Work" shall mean the work of authorship, whether in Source or
      Object form, made available under the License, as indicated by a
      copyright notice that is included in or attached to the work
      (an example is provided in the Appendix below).

      "Derivative Works" shall mean any work, whether in Source or Object
      form, that is based on (or derived from) the Work and for which the
      editorial revisions, annotations, elaborations, or other modifications
      represent, as a whole, an original work of authorship. For the purposes
      of this License, Derivative Works shall not include works that remain
      separable from, or merely link (or bind by name) to the interfaces of,
      the Work and Derivative Works thereof.

      "Contribution" shall mean any work of authorship, including
      the original version of the Work and any modifications or additions
      to that Work or Derivative Works thereof, that is intentionally
      submitted to Licensor for inclusion in the Work by the copyright owner
      or by an individual or Legal Entity authorized to submit on behalf of
      the copyright owner. For the purposes of this definition, "submitted"
      means any form of electronic, verbal, or written communication sent
      to the Licensor or its representatives, including but not limited to
      communication on electronic mailing lists, source code control systems,
      and issue tracking systems that are managed by, or on behalf of, the
      Licensor for the purpose of discussing and improving the Work, but
      excluding communication that is conspicuously marked or otherwise
      designated in writing by the copyright owner as "Not a Contribution."

      "Contributor" shall mean Licensor and any individual or Legal Entity
      on behalf of whom a Contribution has been received by Licensor and
      subsequently incorporated within the Work.

   2. Grant of Copyright License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      copyright license to reproduce, prepare Derivative Works of,
      publicly display, publicly perform, sublicense, and distribute the
      Work and such Derivative Works in Source or Object form.

   3. Grant of Patent License. Subject to the terms and conditions of
      this License, each Contributor hereby grants to You a perpetual,
      worldwide, non-exclusive, no-charge, royalty-free, irrevocable
      (except as stated in this section) patent license to make, have made,
      use, offer to sell, sell, import, and otherwise transfer the Work,
      where such license applies only to those patent claims licensable
      by such Contributor that are necessarily infringed by their
      Contribution(s) alone or by combination of their Contribution(s)
      with the Work to which such Contribution(s) was submitted. If You
      institute patent litigation against any entity (including a
      cross-claim or counterclaim in a lawsuit) alleging that the Work
      or a Contribution incorporated within the Work constitutes direct
      or contributory patent infringement, then any patent licenses
      granted to You under this License for that Work shall terminate
      as of the date such litigation is filed.

   4. Redistribution. You may reproduce and distribute copies of the
      Work or Derivative Works thereof in any medium, with or without
      modifications, and in Source or Object form, provided that You
      meet the following conditions:

      (a) You must give any other recipients of the Work or
          Derivative Works a copy of this License; and

      (b) You must cause any modified files to carry prominent notices
          stating that You changed the files; and

      (c) You must retain, in the Source form of any Derivative Works
          that You distribute, all copyright, patent, trademark, and
          attribution notices from the Source form of the Work,
          excluding those notices that do not pertain to any part of
          the Derivative Works; and

      (d) If the Work includes a "NOTICE" text file as part of its
          distribution, then any Derivative Works that You distribute must
          include a readable copy of the attribution notices contained
          within such NOTICE file, excluding those notices that do not
          pertain to any part of the Derivative Works, in at least one
          of the following places: within a NOTICE text file distributed
          as part of the Derivative Works; within the Source form or
          documentation, if provided along with the Derivative Works; or,
          within a display generated by the Derivative Works, if and
          wherever such third-party notices normally appear. The contents
          of the NOTICE file are for informational purposes only and
          do not modify the License. You may add Your own attribution
          notices within Derivative Works that You distribute, alongside
          or as an addendum to the NOTICE text from the Work, provided
          that such additional attribution notices cannot be construed
          as modifying the License.

      You may add Your own copyright statement to Your modifications and
      may provide additional or different license terms and conditions
      for use, reproduction, or distribution of Your modifications, or
      for any such Derivative Works as a whole, provided Your use,
      reproduction, and distribution of the Work otherwise complies with
      the conditions stated in this License.

   5. Submission of Contributions. Unless You explicitly state otherwise,
      any Contribution intentionally submitted for inclusion in the Work
      by You to the Licensor shall be under the terms and conditions of
      this License, without any additional terms or conditions.
      Notwithstanding the above, nothing herein shall supersede or modify
      the terms of any separate license agreement you may have executed
      with Licensor regarding such Contributions.

   6. Trademarks. This License does not grant permission to use the trade
      names, trademarks, service marks, or product names of the Licensor,
      except as required for reasonable and customary use in describing the
      origin of the Work and reproducing the content of the NOTICE file.

   7. Disclaimer of Warranty. Unless required by applicable law or
      agreed to in writing, Licensor provides the Work (and each
      Contributor provides its Contributions) on an "AS IS" BASIS,
      WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or
      implied, including, without limitation, any warranties or conditions
      of TITLE, NON-INFRINGEMENT, MERCHANTABILITY, or FITNESS FOR A
      PARTICULAR PURPOSE. You are solely responsible for determining the
      appropriateness of using or redistributing the Work and assume any
      risks associated with Your exercise of permissions under this License.

   8. Limitation of Liability. In no event and under no legal theory,
      whether in tort (including negligence), contract, or otherwise,
      unless required by applicable law (such as deliberate and grossly
      negligent acts) or agreed to in writing, shall any Contributor be
      liable to You for damages, including any direct, indirect, special,
      incidental, or consequential damages of any character arising as a
      result of this License or out of the use or inability to use the
      Work (including but not limited to damages for loss of goodwill,
      work stoppage, computer failure or malfunction, or any and all
      other commercial damages or losses), even if such Contributor
      has been advised of the possibility of such damages.

   9. Accepting Warranty or Additional Liability. While redistributing
      the Work or Derivative Works thereof, You may choose to offer,
      and charge a fee for, acceptance of support, warranty, indemnity,
      or other liability obligations and/or rights consistent with this
      License. However, in accepting such obligations, You may act only
      on Your own behalf and on Your sole responsibility, not on behalf
      of any other Contributor, and only if You agree to indemnify,
      defend, and hold each Contributor harmless for any liability
      incurred by, or claims asserted against, such Contributor by reason
      of your accepting any such warranty or additional liability.

   END OF TERMS AND CONDITIONS

   APPENDIX: How to apply the Apache License to your work.

      To apply the Apache License to your work, attach the following
      boilerplate notice, with the fields enclosed by brackets "[]"
      replaced with your own identifying information. (Don't include
      the brackets!)  The text should be enclosed in the appropriate
      comment syntax for the file format. We also recommend that a
      file or class name and description of purpose be included on the
      same "printed page" as the copyright notice for easier
      identification within third-party archives.

   Copyright [yyyy] [name of copyright owner]

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
```

**The project is mid-relicence, and the pinned version is not uniformly Apache-2.0.** The `LICENSE` file at
the exact commit the 1.4.1 nuspec records, `2b7fd35fbe58dfb9f00eae8b3393e1a7361b5e01`, states that
contributions whose authors have not yet granted relicensing consent remain under the MIT License, so at
this pinned version a residual MIT-licensed subset sits inside an otherwise Apache-2.0 package. Reproduced
verbatim from that file:

```
The MCP project is undergoing a licensing transition from the MIT License to the Apache License, Version 2.0 ("Apache-2.0"). All new code and specification contributions to the project are licensed under Apache-2.0. Documentation contributions (excluding specifications) are licensed under CC-BY-4.0.

Contributions for which relicensing consent has been obtained are licensed under Apache-2.0. Contributions made by authors who originally licensed their work under the MIT License and who have not yet granted explicit permission to relicense remain licensed under the MIT License.

No rights beyond those granted by the applicable original license are conveyed for such contributions.
```

A residual MIT-licensed subset therefore ships inside this binary, and MIT's obligation is the
permission notice itself, not the name of the licence. It is reproduced in full here. The copyright
line is the one the shipped package records in its nuspec `<copyright>` field; the MIT-licensed
contributions carry no separate copyright statement inside the package.

```
MIT License

Copyright (c) Model Context Protocol a Series of LF Projects, LLC

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

Apache-2.0 section 4(d) makes NOTICE reproduction a duty only where the work carries a NOTICE file. Checked
and none exists at this pinned version: neither `modelcontextprotocol.1.4.1.nupkg` nor
`modelcontextprotocol.core.1.4.1.nupkg` contains a `NOTICE` entry, and `NOTICE`, `NOTICE.txt` and
`NOTICE.md` all return HTTP 404 at commit `2b7fd35fbe58dfb9f00eae8b3393e1a7361b5e01`. There is therefore
nothing to reproduce under 4(d).

## StreamJsonRpc

`StreamJsonRpc` 2.25.29 carries the JSON-RPC framing for the named-pipe door between the VS Code
extension and the engine. `Semanticus.Engine` references it and `StreamJsonRpc.dll` ships in the engine
payload. Microsoft publishing it is not a licence exemption: it ships under MIT, so the permission notice
travels with it.

- Path: `StreamJsonRpc`
- License: **MIT**
- Author: Microsoft Corporation
- Version: **2.25.29**
- Source: <https://github.com/microsoft/vs-streamjsonrpc>

Read from `LICENSE` at commit `332cc170a82fa424e0b4f902ab9f9a876e827f38`, the commit recorded in the
`<repository>` element of the shipped `streamjsonrpc.nuspec`. Reproduced verbatim:

```
StreamJsonRpc
Copyright (c) Microsoft Corporation
All rights reserved. 

MIT License

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

One detail of that text is invisible and deliberate: the `All rights reserved.` line ends with a
NO-BREAK SPACE (U+00A0), not a normal space, because the upstream file does. It is reproduced rather than
tidied. Do not strip it; the reproduction stops being verbatim if you do.

The package also carries a 481,172-byte `NOTICE` file aggregating notices for material Microsoft
incorporates across that repository's build. It is not reproduced here: MIT creates no NOTICE
obligation, and that file is not copied into the engine payload. It is available in the package at
`StreamJsonRpc/2.25.29/NOTICE`.

## Azure.Identity

`Azure.Identity` 1.13.1 supplies the Microsoft Entra token credentials used to authenticate XMLA
connections. `Semanticus.Engine` references it and `Azure.Identity.dll` ships in the engine payload. It is
MIT, so its notice is carried here rather than treated as exempt because Microsoft publishes it.

- Path: `Azure.Identity`
- License: **MIT**
- Author: Microsoft
- Version: **1.13.1**
- Source: <https://github.com/Azure/azure-sdk-for-net>

Read from the repository-root `LICENSE.txt` at commit `fd0b3e70a336accb1bae2e2ffc45de52ae688710`, the
commit recorded in the `<repository>` element of the shipped `azure.identity.nuspec`. The package ships no
licence file of its own, and there is no per-library `LICENSE.txt` beside the library at that commit.
Reproduced verbatim:

```
The MIT License (MIT)

Copyright (c) 2015 Microsoft

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

## Microsoft.Data.SqlClient

`Microsoft.Data.SqlClient` 7.0.2 is the SQL client used when reading source schema. `Semanticus.Engine`
references it and `Microsoft.Data.SqlClient.dll` ships in the engine payload, together with
`Microsoft.Data.SqlClient.SNI`, `.Extensions.Abstractions` and `.Internal.Logging`. Being a Microsoft
redistributable client library is not a licence ground: the package declares MIT.

- Path: `Microsoft.Data.SqlClient`
- License: **MIT**
- Author: .NET Foundation
- Version: **7.0.2**
- Source: <https://github.com/dotnet/sqlclient>

The package ships no licence file inside the `.nupkg`, so the licence was read from `LICENSE` at commit
`8c70cec98444338ddb0b97be94c34fde93970241`, the commit recorded in the `<repository>` element of the
shipped `microsoft.data.sqlclient.nuspec`. That file is indented by four spaces and its final line ends
without a full stop; it is reproduced exactly as published, not tidied:

```
    MIT License

    Copyright (c) .NET Foundation. All rights reserved.

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
    SOFTWARE
```

## .NET runtime library packages (dotnet/runtime)

Five NuGet packages built from `dotnet/runtime` ship as separate assemblies in the engine payload. They are
**not** part of the .NET shared framework and are not supplied by the runtime pack, so a
"shared-framework family" reading does not exempt them. They are separately licensed payload under MIT and
their notice is carried here.

- Path: `Microsoft.Extensions.Hosting`
- Path: `System.CodeDom`
- Path: `System.Data.OleDb`
- Path: `System.Security.Permissions`
- Path: `System.Management`
- License: **MIT**
- Author: .NET Foundation and Contributors
- Version: **8.0.1** (`Microsoft.Extensions.Hosting`)
- Version: **8.0.0** (`System.CodeDom`, `System.Data.OleDb`, `System.Security.Permissions`, `System.Management`)
- Source: <https://github.com/dotnet/runtime>

Every one of the five packages carries its own `LICENSE.TXT` inside the `.nupkg`, and all five are
byte-identical (SHA-256 `d7a68596ab69b06f...`, 1,139 bytes each), so one reproduction covers all five.
Read from `LICENSE.TXT` inside the cached `microsoft.extensions.hosting.8.0.1.nupkg`. Reproduced verbatim:

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

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

Each of the five also ships its own `THIRD-PARTY-NOTICES.TXT` describing material `dotnet/runtime` itself
incorporates. Its own opening states the notices "are provided for information only". It is not reproduced
here and it is not copied into the engine payload.

**These five notice files are NOT identical, and this section used to say they were.** The licence texts
are identical; the third-party notices are not. Measured over the cached packages:

- `Microsoft.Extensions.Hosting` 8.0.1: 68,910 bytes, names **zlib version 1.3.1, January 22nd, 2024**.
- `System.CodeDom`, `System.Data.OleDb`, `System.Security.Permissions`, `System.Management`, all 8.0.0:
  68,911 bytes each, byte-identical to each other, and they name **zlib version 1.2.13, October 13th,
  2022**.

That one zlib version line is the whole difference: a byte-wise diff of Hosting's file against a 8.0.0 one
reports a single changed line. Nothing else in either file differs. The claim being corrected mattered
because "identical" was the reason one reproduction was said to cover all five, and for the `LICENSE.TXT`
files that reason still holds exactly as stated above.

The self-contained publish also copies the whole .NET 8 runtime pack into the payload, and the packages
named above pull transitive packages of their own that no section here describes. Both are wider questions
than these five packages, both are open items on the board, and neither is resolved by this section.

## Webview bundled dependencies — `Semanticus.VSCode/media/studio/studio.js`

`Semanticus.VSCode/media/studio/studio.js` is a **tracked build output**, so the third-party code bundled
into it is published with this repository. It is built from the webview's production dependency closure,
recorded in `third-party-manifest.json` and reproduced here.

- Version: **generated from Semanticus.VSCode/webview/package-lock.json**
- License: **multiple: 0BSD, Apache-2.0, BSD-3-Clause, ISC, MIT**
- Project: <https://github.com/microsoft/powerquery-parser> (and the other upstreams listed below)

**Scope of this record.** These are the 53 non-dev packages the lockfile pins, which is what the bundler is
given. It is NOT a claim about which packages survived tree-shaking into the emitted file, and 8 of them are
`@types/*` packages that emit no runtime code at all. Per-package presence in the emitted bundle is not
proven and is not claimed. The attribution gate compares this list against the lockfile in both directions,
so a new production dependency cannot arrive without being recorded here.

| Package | Version | License |
|---|---|---|
| `@codemirror/autocomplete` | 6.20.3 | MIT |
| `@codemirror/commands` | 6.10.4 | MIT |
| `@codemirror/language` | 6.12.4 | MIT |
| `@codemirror/lint` | 6.9.7 | MIT |
| `@codemirror/state` | 6.7.0 | MIT |
| `@codemirror/view` | 6.43.3 | MIT |
| `@dagrejs/dagre` | 1.1.8 | MIT |
| `@dagrejs/graphlib` | 2.2.4 | MIT |
| `@lezer/common` | 1.5.2 | MIT |
| `@lezer/highlight` | 1.2.3 | MIT |
| `@lezer/lr` | 1.4.10 | MIT |
| `@marijn/find-cluster-break` | 1.0.2 | MIT |
| `@microsoft/powerquery-formatter` | 1.0.0 | MIT |
| `@microsoft/powerquery-language-services` | 1.0.0 | MIT |
| `@microsoft/powerquery-parser` | 0.19.0 | MIT |
| `@tanstack/react-virtual` | 3.14.3 | MIT |
| `@tanstack/virtual-core` | 3.17.1 | MIT |
| `@types/d3-color` | 3.1.3 | MIT |
| `@types/d3-drag` | 3.0.7 | MIT |
| `@types/d3-interpolate` | 3.0.4 | MIT |
| `@types/d3-selection` | 3.0.11 | MIT |
| `@types/d3-transition` | 3.0.9 | MIT |
| `@types/d3-zoom` | 3.0.8 | MIT |
| `@types/react` | 19.2.17 | MIT |
| `@types/react-dom` | 19.2.3 | MIT |
| `@xyflow/react` | 12.11.1 | MIT |
| `@xyflow/system` | 0.0.78 | MIT |
| `classcat` | 5.0.5 | MIT |
| `crelt` | 1.0.6 | MIT |
| `csstype` | 3.2.3 | MIT |
| `d3-color` | 3.1.0 | ISC |
| `d3-dispatch` | 3.0.1 | ISC |
| `d3-drag` | 3.0.0 | ISC |
| `d3-ease` | 3.0.1 | BSD-3-Clause |
| `d3-interpolate` | 3.0.1 | ISC |
| `d3-selection` | 3.0.0 | ISC |
| `d3-timer` | 3.0.1 | ISC |
| `d3-transition` | 3.0.1 | ISC |
| `d3-zoom` | 3.0.0 | ISC |
| `echarts` | 6.1.0 | Apache-2.0 |
| `grapheme-splitter` | 1.0.4 | MIT |
| `performance-now` | 2.1.0 | MIT |
| `react` | 19.2.7 | MIT |
| `react-dom` | 19.2.7 | MIT |
| `scheduler` | 0.27.0 | MIT |
| `style-mod` | 4.1.3 | MIT |
| `tslib` | 2.3.0 | 0BSD |
| `use-sync-external-store` | 1.6.0 | MIT |
| `vscode-languageserver-textdocument` | 1.0.12 | MIT |
| `vscode-languageserver-types` | 3.17.5 | MIT |
| `w3c-keyname` | 2.2.8 | MIT |
| `zrender` | 6.1.0 | BSD-3-Clause |
| `zustand` | 4.5.7 | MIT |

### Copyright holders for the non-MIT families

Read from the installed packages on 2026-07-29:

- `zrender` — BSD-3-Clause, Copyright (c) 2017, Baidu Inc.
- `d3-ease` — BSD-3-Clause, Copyright 2010-2021 Mike Bostock; Copyright 2001 Robert Penner
- `echarts` — Apache-2.0, Copyright 2017-2026 The Apache Software Foundation
- `tslib` — 0BSD, Copyright (c) Microsoft Corporation
- `react`, `react-dom`, `scheduler` — MIT, Copyright (c) Meta Platforms, Inc. and affiliates
- `d3-color`: ISC, Copyright 2010-2022 Mike Bostock (re-read 2026-08-18)
- `d3-dispatch`, `d3-drag`, `d3-interpolate`, `d3-selection`, `d3-timer`, `d3-transition`, `d3-zoom`: ISC,
  Copyright 2010-2021 Mike Bostock (re-read 2026-08-18)
- `@microsoft/powerquery-*` — MIT, Copyright (c) Microsoft Corporation

### Apache-2.0 NOTICE, required by section 4(d)

`echarts` ships an upstream `NOTICE` file, so Apache-2.0 section 4(d) requires its content to be carried in
distributions that include the work. Reproduced verbatim:

```
Apache ECharts
Copyright 2017-2026 The Apache Software Foundation

This product includes software developed at
The Apache Software Foundation (https://www.apache.org/).
```

`echarts` also vendors a d3 licence at `licenses/LICENSE-d3` within its own package.

### Apache License 2.0

Apache-2.0 is an invariant document, so the full text reproduced verbatim in the Model Context
Protocol C# SDK section above is the same text that governs `echarts`. This document ships whole, so
that one reproduction discharges the obligation for both; it is not an external link standing in for
the text. The canonical copy is at <https://www.apache.org/licenses/LICENSE-2.0>, and the installed
package carries its own copy at `Semanticus.VSCode/webview/node_modules/echarts/LICENSE`.

### BSD 3-Clause License (zrender 6.1.0)

Applies to `zrender` only. BSD-3-Clause is a template, not an invariant document: each project fills in
its own wording for the non-endorsement clause and the liability clause, so one reproduction does not
discharge the obligation for another BSD-3-Clause package. `d3-ease` ships different wording and gets its
own text below. Read from `Semanticus.VSCode/webview/node_modules/zrender/LICENSE`, re-wrapped, 1,420
characters of operative text once whitespace is collapsed. Copyright (c) 2017, Baidu Inc., all rights
reserved, stated here as well as in the holders table above, because BSD-3-Clause requires the copyright
notice to be retained beside the conditions it belongs to.

```
Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.
* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.
* Neither the name of the copyright holder nor the names of its contributors
  may be used to endorse or promote products derived from this software without
  specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### BSD 3-Clause License (d3-ease 3.0.1)

Applies to `d3-ease` only. Its wording differs from zrender's in two operative places: the
non-endorsement clause names "the author" rather than "the copyright holder", and the liability clause
names the "COPYRIGHT OWNER" rather than the "COPYRIGHT HOLDER". Reproduced verbatim from
`Semanticus.VSCode/webview/node_modules/d3-ease/LICENSE` at the installed version 3.0.1, which is the
version the webview lockfile pins. 1,405 characters of operative text once
whitespace is collapsed. Copyright 2010-2021 Mike Bostock; Copyright 2001 Robert Penner; all rights
reserved.

```
Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice,
  this list of conditions and the following disclaimer in the documentation
  and/or other materials provided with the distribution.

* Neither the name of the author nor the names of contributors may be used to
  endorse or promote products derived from this software without specific prior
  written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

### ISC License (d3-color 3.1.0)

TWO ISC SECTIONS, NOT ONE, and the split is the notice and not the licence. The ISC body below is
byte-identical across all eight installed `d3-*` ISC packages: normalized SHA-256
`4829cc8e654be69dd1edcebedbda21758a239c230e38f532d6bf4b0adc1a0427` from each of their `LICENSE` files,
measured 2026-08-18. What differs is the copyright line the licence requires to travel with it, and this
document used to reduce all eight to "Copyright Mike Bostock". That shortened line is in no installed
package, so it was not the notice any of them ship. `d3-color` states the year range below; the other seven
state a different one and have their own section after this.

Copyright line reproduced from `webview/node_modules/d3-color/LICENSE`, read 2026-08-18:

```
Copyright 2010-2022 Mike Bostock

Permission to use, copy, modify, and/or distribute this software for any purpose
with or without fee is hereby granted, provided that the above copyright notice
and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS
OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER
TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF
THIS SOFTWARE.
```

### ISC License (d3-dispatch, d3-drag, d3-interpolate, d3-selection, d3-timer, d3-transition, d3-zoom)

The same ISC body, carrying the copyright line these seven packages actually ship. Reproduced from
`webview/node_modules/d3-dispatch/LICENSE`, read 2026-08-18; the other six are byte-identical to it.

```
Copyright 2010-2021 Mike Bostock

Permission to use, copy, modify, and/or distribute this software for any purpose
with or without fee is hereby granted, provided that the above copyright notice
and this permission notice appear in all copies.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS
OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER
TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF
THIS SOFTWARE.
```

### BSD Zero Clause License (0BSD)

Applies to `tslib`. Copyright (c) Microsoft Corporation, read from the installed package on 2026-07-29 and
stated here as well as in the holders table above, so the notice carries its own copyright line.

```
Permission to use, copy, modify, and/or distribute this software for any purpose
with or without fee is hereby granted.

THE SOFTWARE IS PROVIDED "AS IS" AND THE AUTHOR DISCLAIMS ALL WARRANTIES WITH
REGARD TO THIS SOFTWARE INCLUDING ALL IMPLIED WARRANTIES OF MERCHANTABILITY AND
FITNESS. IN NO EVENT SHALL THE AUTHOR BE LIABLE FOR ANY SPECIAL, DIRECT,
INDIRECT, OR CONSEQUENTIAL DAMAGES OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS
OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER
TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF
THIS SOFTWARE.
```

The MIT License text reproduced in the Tabular Editor 2 section above is the same MIT text that applies to
the MIT packages listed here, with each package's own copyright holder as named above.

## Power Query M standard-library symbols — `Semanticus.VSCode/webview/src/mstdlib.ts`

The vendored M standard-library symbol dataset (866 built-in functions/constants/types — `Table.*`, `List.*`,
`Text.*`, `Date.*`, `Sql.Database`, …) that enriches M autocomplete + hover is copied verbatim from the
**vscode-powerquery** extension's `server/src/library/standard/standard-enUs.json` (pinned commit
`6a43c83b7ab8adc6e03bdb82c647030ce2ed041f`), re-embedded as a `JSON.parse` string module.

- Source: <https://github.com/microsoft/vscode-powerquery>
- License: **MIT** — Copyright (c) Microsoft Corporation
