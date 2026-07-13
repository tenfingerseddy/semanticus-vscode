# Packaging the Semanticus extension

Kane's decision: ship **platform-specific** `.vsix` files (one per OS/arch via `vsce --target`), **not** one fat
cross-platform vsix. Each vsix bundles a **self-contained** `Semanticus.Engine` publish for exactly one .NET RID
under `extension/engine/`, so the installed extension spawns the engine **directly** — the user needs **no .NET
runtime** installed. At runtime `resolveEngine()` finds that bundled exe (kind `'exe'`) and launches it without
`dotnet`; a developer's `semanticus.engineDll` override still wins for the F5 dev loop (kind `'dll'`, run via
`dotnet`).

## Targets

| `--target` (vsce)  | .NET RID    | Bundled binary                     | ~Compressed size |
|--------------------|-------------|------------------------------------|------------------|
| `win32-x64`        | `win-x64`   | `engine/Semanticus.Engine.exe`     | ~51 MB (measured 50.5 MB) |
| `win32-arm64`      | `win-arm64` | `engine/Semanticus.Engine.exe`     | ~50 MB           |
| `linux-x64`        | `linux-x64` | `engine/Semanticus.Engine`         | ~50 MB           |
| `darwin-x64`       | `osx-x64`   | `engine/Semanticus.Engine`         | ~50 MB           |
| `darwin-arm64`     | `osx-arm64` | `engine/Semanticus.Engine`         | ~50 MB           |

The verified 2026-07-13 Windows x64 build contains a 120.6 MB / 565-file engine payload and produces a
**50.5 MB, 612-entry** VSIX. Debug PDBs are excluded.

## Usage

```bash
cd Semanticus.VSCode
npm ci                            # locked root tooling, including @vscode/vsce
npm run package:win               # win32-x64 shorthand
node scripts/package.mjs win32-x64
node scripts/package.mjs all      # every target above
```

`scripts/package.mjs` (pure node, no extra deps): builds the platform-agnostic webview + TS **once**, then per
target, cleans `engine/`, runs `dotnet publish -c Release -r <rid> --self-contained -o engine/`, packages with
`vsce --target`, verifies the finished archive, and cleans `engine/` again so a RID never leaks into the next
build. Outputs land in `dist/`. Both `engine/` and `dist/` are git-ignored.

`verify-vsix.mjs` opens the actual VSIX and allows only its two package metadata files plus `out/`, `media/`,
`engine/`, `language/`, `package.json`, `README.md`, `LICENSE`, `NOTICE`, and the production
`node_modules/vscode-jsonrpc` subtree. It rejects source, tests, internal/build directories, logs, maps, PDBs,
credential-like files, known credential dotfiles and credential directories, symbolic links, path traversal,
duplicates, and case collisions. Every archive file is also opened and scanned for private-key material,
high-confidence token formats, hardcoded service secrets, and model-provider inference or credential paths inside
the engine payload; an incomplete content scan fails the package. It also proves the
target manifest, production entry point, self-contained runtime files, engine workflows, and minimum runtime
payload are present. When the package target matches the host, it extracts the engine from the finished VSIX
and launches that exact binary directly with global .NET lookup disabled. CI and the human-triggered publication
workflow build and execute this check on Windows x64 before retaining or publishing the artifact.

### `vsce` notes
- `LICENSE` + `NOTICE` ship in this folder (Elastic License 2.0 + the vendored-TE/Microsoft-client-library attributions).
- `publisher` is **`semanticus-vscode`** — Kane's registered Marketplace publisher id (2026-07-04; plain
  `semanticus` was taken). Publishing needs an Azure DevOps PAT from the same Microsoft account with
  Organization = **All accessible organizations** and the **Marketplace → Manage** scope.
- `README.md` here IS the marketplace store page — its images must stay absolute URLs on
  semanticus.com.au (the repo is private, so raw.githubusercontent links would 404 for the public).

## Connect Claude Code (the .mcp.json auto-writer)

Command **Semanticus: Connect Claude Code (write .mcp.json)** writes/merges `<workspace>/.mcp.json` so the user's
Claude Code attaches to this engine over MCP. The engine is **attach-or-own** (ONE server model): the `mcp`
process attaches to a running owner engine (the one VS Code Studio spawned) when present, else owns the model
itself. The merge is **non-destructive** — every other `mcpServers` entry is preserved; only `semanticus` is set.
A malformed existing `.mcp.json` is never clobbered (it fails loud and offers to open the file).

Entry shape follows `resolveEngine()`:

- **Bundled exe** — `command` = the exe path, `args` = `["mcp","--workspace",<ws>]`.
- **Dev DLL override** — `command` = the resolved `dotnet`, `args` = `[<dll>,"mcp","--workspace",<ws>]`.

If `semanticus.licenseToken` is set, `["--license", <token>]` is appended (the reliable Pro-entitlement channel;
`.mcp.json` env blocks are historically unreliable, which is why the flag exists).

Sample (bundled exe, no license):

```json
{
  "mcpServers": {
    "semanticus": {
      "command": "C:\\Users\\me\\.vscode\\extensions\\semanticus\\engine\\Semanticus.Engine.exe",
      "args": ["mcp", "--workspace", "C:\\work\\my-model"]
    }
  }
}
```
