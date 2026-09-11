# uishot — headless screenshots of the Semanticus webview UI

Render the Semanticus webviews in an isolated headless Chromium with the engine **mocked**, then screenshot
them — so UI changes can be reviewed visually (read the PNG) without an Extension Development Host. A fast
self-review loop: build → screenshot → look, before handing to Kane.

Covers both webviews:
- **studio** — the React Studio dashboard (`media/studio`): every tab.
- **propgrid** — the Properties grid docked under the Model tree (`media/propgrid`): several scenarios.

(The Model **tree**, status bar, and menus are native VS Code — not webviews — so they can't be captured this
way.)

## Use

**Needs Node 22.12 or newer.** That floor is `puppeteer-core` 25's own (`engines.node >= 22.12.0`), declared
in this package's `engines` too, and every script here checks it at startup and says so plainly instead of
failing somewhere inside puppeteer. It is not negotiable downwards: measured on the registry 2026-08-18, the
newest `puppeteer-core` 24.x still pulls `extract-zip` and `ip-address` and `npm audit` reports 3 advisories,
while every version that drops them declares this floor. Clean audit and Node 20 are not both available.

No CI job installs or runs this directory, so the floor binds local runs only. CI touches this package in
exactly one way: `Semanticus.VSCode/test/dependency-audit.test.mjs` finds all four `package-lock.json` roots
under `Semanticus.VSCode/` and runs `npm audit` in each, on Node 20. `npm audit` reads the lockfile and does
not install anything or evaluate `engines`, so it is unaffected.

**`npm install` here does NOT download a browser.** This package depends on `puppeteer-core`, which is the
library without the bundled Chromium. Install a shell once with
`npx @puppeteer/browsers install chrome-headless-shell@stable` (it lands in `~/.cache/puppeteer`), or point
`SEMANTICUS_BROWSER` at a Chromium executable. When several versions are cached the **newest** is used, and
the choice is printed; it used to be whichever the filesystem listed first.

```bash
cd Semanticus.VSCode/tools/uishot
npm install                          # puppeteer-core only. It downloads NO browser -- see above.
npm run build:webview --prefix ../..  # rebuild media/studio first if you changed the React webview
                                      # (propgrid assets are static — no build needed)

node shot.mjs                         # studio Diagram -> shots/studio-diagram.png
node shot.mjs "AI Readiness"          # a studio tab by label
node shot.mjs studio Diagram out.png  # explicit target + custom output
node shot.mjs propgrid measure        # Properties grid scenario -> shots/propgrid-measure.png
node shot.mjs all                     # EVERYTHING: every studio tab + Connections states + every propgrid scenario
UISHOT_GRAPH=mini node shot.mjs Diagram   # focused 3-relationship graph (cardinality markers render large)
UISHOT_GRAPH=docstress UISHOT_DOC_SECTION=diagram node shot.mjs Docs   # 34-table exported relationship diagram
UISHOT_VE=broken node shot.mjs "Edit History"   # Verified-Edits audit trail with a TAMPERED (broken) hash chain
UISHOT_PQ=incremental node shot.mjs "M Code"   # missing incremental-refresh prerequisites + repair actions

node pageshot.mjs page.html out.png [widthPx]  # screenshot ANY local HTML file (mockups, wireframes, docs)
```

- **studio** variants (tab labels): `AI Readiness · Optimize · BPA · Diagram · Storage · Data · DAX Query · DAX Lab · Pivot`
- **propgrid** variants (scenarios): `model` (the default no-object selection and its model settings) · `measure` (all editor kinds) · `multi` (multi-select / varies) · `column`
  (data-type dropdown + the Format String picker open) · `formatexpr` (the Format expression editor open) ·
  `lowcl` (Format expression locked below compatibility level 1601) · `staledraft` (an open editor + draft must
  NOT survive onto a same-named object with a different ref key) · `error` (inline validation) · `empty`

## How it works (and why)

- **Isolated Chromium, not Edge.** The machine's Edge hands off to the user's running instance, so it can't be
  driven headlessly. `shot.mjs` uses the cached **`chrome-headless-shell`** in `~/.cache/puppeteer`, which
  `npm install` does NOT put there (see above); install it once with `npx @puppeteer/browsers install
  chrome-headless-shell@stable`. The newest cached build **for this platform** is used. Override with
  `SEMANTICUS_BROWSER=<chromium.exe>`.
- **Served over HTTP, not file://.** Headless Chromium refuses to screenshot `file://`, so `shot.mjs` serves
  the extension dir on a throwaway `127.0.0.1` server and points the browser at it.
- **Engine mocked in the page.** Each harness HTML stubs `acquireVsCodeApi()` and answers the webview's
  RPC / messages with fixtures:
  - [`harness.html`](harness.html) — studio: `sessionInfo`, `getModelGraph`, `aiReadinessScan`, `bpaScan`,
    `vertiPaqScan`, … Override via `window.__UISHOT__` or `?g=mini`. Query tabs show their honest
    "needs a live engine" state (`connectionStatus.connected=false`).
  - [`propgrid.html`](propgrid.html) — Properties grid: answers the grid's `ready` with a `load` of fixture
    props (+ a `formatTemplates` catalog slice). Scenario via `?s=model|measure|multi|column|formatexpr|lowcl|staledraft|error|empty`.
- **Single source of truth.** `media/propgrid/{propgrid.css,propgrid.js}` are loaded by BOTH the extension's
  `PropertyGridProvider` and this harness — so a screenshot is the real grid, not a copy that can drift.
- **Real waits.** Waits for each view to mount (and, for Diagram, for nodes + edges to paint) before capturing.

`node_modules/` and `shots/` are git-ignored. Only the harnesses, script, and `package*.json` are tracked.
