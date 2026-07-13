# Studio UI harness (headless screenshots)

Renders the **built** Studio webview bundle (`../media/studio/studio.{js,css}`) in headless
Chromium with a **mocked engine**, so the UI can be reviewed and iterated on without launching
VS Code (F5). Each tab is driven (tabs clicked, action buttons pressed) and saved as a PNG.

This is how Claude "sees" the UI: build the webview, run this, then open the PNGs in `shots/`.

## Use

```sh
cd Semanticus.VSCode/webview && npm run build      # produce media/studio/studio.{js,css}
cd ../uitest && npm install && npx playwright install chromium   # first time only
node shoot.mjs            # all tabs -> shots/*.png
node shoot.mjs "Pivot"    # just one tab
```

## How it works

- `harness.html` defines a fake `acquireVsCodeApi()` whose `postMessage` answers every `{type:'rpc'}`
  request with a canned fixture (see the `FIX` map), then loads the real `studio.js`. It also pins
  representative `--vscode-*` theme variables so it looks like the dark host.
- `shoot.mjs` loads the harness over `file://`, clicks each tab + the button that populates it
  (Scan storage / Run / Benchmark / Pivot / a table row), and screenshots to `shots/`.

To exercise a new view, add its RPC method + a fixture to `FIX` in `harness.html`, and a tab entry
(with any needed `after()` clicks) in `shoot.mjs`.

> Not a substitute for a real F5 check — fonts/theme/live data differ — but it catches layout,
> spacing, formatting and component bugs fast. (It already caught year keys being thousands-grouped
> in the pivot matrix.)
