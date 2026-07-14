# Semanticus

Semanticus is a semantic-model workbench in VS Code for Fabric and Power BI. A human works through the Model tree,
Properties and Studio while their own AI Assistant uses MCP against the same engine session. Both doors share one
model, one change stream and one undo timeline.

Semanticus runs no inference and stores no model-provider API credentials. The AI Assistant is a separate MCP
client selected and operated by the user.

## Release status

Semanticus 1.0.1 corrects the Marketplace listing and provides five platform-specific packages. Every accepted
VSIX bundles its engine and is produced only on a matching GitHub Actions runner that extracts and executes that
engine. Tenant-backed and clean-machine acceptance cannot be replaced by a local build and must be completed
against the artifact that is published.

## Install

Install Semanticus from the VS Code Marketplace, which selects the package for the current host. For a manual
install, choose the matching 1.0.1 VSIX, then in VS Code open **Extensions**, choose **Views and More Actions**,
select **Install from VSIX**, and reload the window. Semanticus includes its .NET engine, so users of an accepted
package do not need to install a separate .NET runtime.

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

## Supported platforms for 1.0.1

| Platform | 1.0.1 status | Scope |
|---|---|---|
| Windows 11 x64 | Supported release package | Offline file models, Power BI Desktop discovery, local and remote XMLA, local M preview, VS Code UI and MCP |
| Windows 11 ARM64 | Supported release package | Offline file models, remote XMLA and Fabric journeys, VS Code UI and MCP |
| Ubuntu 24.04 x64 | Supported release package | Offline file models, remote XMLA and Fabric journeys, VS Code UI and MCP |
| macOS Intel | Supported release package | Offline file models, remote XMLA and Fabric journeys, VS Code UI and MCP |
| macOS Apple Silicon | Supported release package | Offline file models, remote XMLA and Fabric journeys, VS Code UI and MCP |

Remote XMLA and Fabric features require a compatible tenant, capacity, permissions and credentials.
Power BI Desktop discovery, local XMLA and local M preview remain Windows 11 x64 only.

## Frozen 1.0 surface

- Dual-drive model editing through the VS Code UI and MCP, with attributed changes, broadcast and undo.
- Offline BIM, PBIP and TMDL model work, plus supported live XMLA journeys on Windows.
- DAX and M authoring, model properties, diagrams, advanced modelling and modern calendars at compatibility level
  1701 or later.
- Best Practice Analyzer, AI Readiness, DAX rules, Verified Edits, Tests and evidence reports.
- Lineage and impact for model metadata and supported local report artifacts.
- Workflows, Change Plans, Data Agent schema authoring, DaxLib and multi-model compare/copy.
- Dry-run-first deployment and Fabric discovery surfaces within the documented boundary.

Cloud report discovery is supported behind consent. Fabric ALM writes and Data Agent publishing preview as a dry
run and write only on explicit confirmation. Role impersonation and automatic classic-calendar migration remain
deferred. Unsupported metadata must fail closed without damaging the source model.

## Build from a clean clone

The Tabular Editor TOMWrapper donor is pinned as a Git submodule under `external/TabularEditor`.

```powershell
git clone --recurse-submodules https://github.com/tenfingerseddy/semanticus-vscode.git
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
