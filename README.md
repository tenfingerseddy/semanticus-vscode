# Semanticus

Semanticus is a semantic-model workbench in VS Code for Fabric and Power BI. A human works through the Model tree,
Properties and Studio while their own AI Assistant uses MCP against the same engine session. Both doors share one
model, one change stream and one undo timeline.

Semanticus runs no inference and stores no model-provider API credentials. The AI Assistant is a separate MCP
client selected and operated by the user.

## Release status

Semanticus 1.0.0 is the first public Windows 11 x64 release. Its platform-specific VSIX bundles the engine and is
accepted only when the exact commit passes the Ubuntu, Windows and runnable-VSIX CI gates. Tenant-backed and
clean-machine acceptance cannot be replaced by a local build and must be completed against the artifact that is
published.

## Install on Windows

Download `semanticus-win32-x64-1.0.0.vsix` from the GitHub release, then in VS Code open **Extensions**, choose
**Views and More Actions**, select **Install from VSIX**, and reload the window. Semanticus includes its .NET engine,
so users of the packaged extension do not need to install a separate .NET runtime.

## Architecture

```text
VS Code extension (TypeScript) ---- JSON-RPC / named pipe ---+
                                                             +--> Semanticus.Engine (.NET 8)
AI Assistant ---------------------- MCP / stdio -------------+      single-writer model dispatcher
                                                                    SessionManager + ChangeBus
                                                                    TOM + ADOMD
```

Both doors call the same engine contracts. A successful change broadcasts `model/didChange`, carries its origin,
and participates in the shared undo history.

## Supported platforms for 1.0

| Platform | 1.0 status | Scope |
|---|---|---|
| Windows 11 x64 | Supported release platform | Offline file models, Power BI Desktop discovery, local and remote XMLA, local M preview, VS Code UI and MCP |
| Ubuntu 24.04 x64 | Source and CI coverage only | CI proves build, tests and offline engine journeys; there is no supported 1.0 VSIX claim |
| macOS | Not supported in 1.0 | Build artifacts may exist, but there is no accepted clean-machine product journey |

Remote XMLA and Fabric features require a compatible tenant, capacity, permissions and credentials.

## Frozen 1.0 surface

- Dual-drive model editing through the VS Code UI and MCP, with attributed changes, broadcast and undo.
- Offline BIM, PBIP and TMDL model work, plus supported live XMLA journeys on Windows.
- DAX and M authoring, model properties, diagrams, advanced modelling and modern calendars at compatibility level
  1701 or later.
- Best Practice Analyzer, AI Readiness, DAX rules, Verified Edits, Tests and evidence reports.
- Lineage and impact for model metadata and supported local report artifacts.
- Workflows, Change Plans, Data Agent schema authoring, DaxLib and multi-model compare/copy.
- Dry-run-first deployment and Fabric discovery surfaces within the documented boundary.

Cloud report discovery, Fabric ALM writes beyond dry-run, Data Agent publishing, role impersonation and automatic
classic-calendar migration are deferred. Unsupported metadata must fail closed without damaging the source model.

## Build from a clean clone

The Tabular Editor TOMWrapper donor is pinned as a Git submodule under `external/TabularEditor`.

```powershell
git clone --recurse-submodules https://github.com/tenfingerseddy/semanticus-studio.git
dotnet build Semanticus.sln
dotnet test Semanticus.Tests/Semanticus.Tests.csproj
dotnet run --project Semanticus.Smoke
dotnet run --project Semanticus.RpcSmoke
dotnet run --project Semanticus.McpSmoke
dotnet run --project Semanticus.AirSmoke
```

Building from source requires the .NET 8 SDK, Node.js 20 or later, and JDK 17 for ANTLR code generation. The
platform-specific production VSIX bundles a self-contained engine, so an installed user does not need a separate
.NET runtime.

## Run the extension in development

```powershell
dotnet build Semanticus.Engine -c Debug
Set-Location Semanticus.VSCode
npm ci --no-fund --no-audit
npm test
npm run build:webview
code .
```

Select **Run Semanticus Extension** and press F5. In the Extension Development Host, set `semanticus.engineDll` to
the built Debug DLL, run **Semanticus: Open Model**, then open Studio. Run **Semanticus: Connect AI Assistant** to
write the local MCP entry for the current workspace. Keep `.mcp.json` ignored and never commit a license token.

## Privacy and security boundary

- The engine performs no inference and contains no model SDK or model-provider credential path.
- Model changes stay local unless the user explicitly opens a data connection or confirms a live write.
- Live writes run a dry-run preview first and require a human confirmation where documented.
- License minting and private signing keys are not part of this repository or extension.
- The release VSIX is allow-listed and must pass payload verification before clean-machine acceptance.

## Release notes

See [`CHANGELOG.md`](CHANGELOG.md) for the shipped feature and fix history.
