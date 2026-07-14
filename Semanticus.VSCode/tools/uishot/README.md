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

```bash
cd Semanticus.VSCode/tools/uishot
npm install                          # one-time: puppeteer-core + downloads chrome-headless-shell
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
```

- **studio** variants (tab labels): `AI Readiness · Optimize · BPA · Diagram · Statistics · Data · DAX Query · DAX Lab · Pivot`
- **propgrid** variants (scenarios): `model` (the default no-object selection and its model settings) · `measure` (all editor kinds) · `multi` (multi-select / varies) · `column`
  (data-type dropdown + the Format String picker open) · `formatexpr` (the Format expression editor open) ·
  `lowcl` (Format expression locked below compatibility level 1601) · `staledraft` (an open editor + draft must
  NOT survive onto a same-named object with a different ref key) · `error` (inline validation) · `empty`

## How it works (and why)

- **Isolated Chromium, not Edge.** The machine's Edge hands off to the user's running instance, so it can't be
  driven headlessly. `shot.mjs` uses puppeteer's cached **`chrome-headless-shell`** (downloaded by
  `npm install`). Override with `SEMANTICUS_BROWSER=<chromium.exe>`.
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
