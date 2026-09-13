# Semanticus assistant skills

Semanticus 1.1.3 includes seven guides that help an AI assistant find the right tools and work with
the open model. They use your existing Semanticus MCP connection and load when relevant to a task.
The engine also supplies essential startup guidance through MCP, so skills are optional.

## Install from the workbench

Run **Semanticus: Install or Update Assistant Skills** from the VS Code Command Palette.
You can also choose **Install assistant skills** after running **Connect AI Assistant**.

| Assistant | Installation |
|---|---|
| GitHub Copilot | Bundled skills are registered by the Semanticus extension in VS Code versions supporting extension-provided skills. |
| Claude Code | Choose Claude Code to install user skills under `~/.claude/skills/`. |
| Codex | Choose Codex to install user skills under `~/.agents/skills/`. |

The `~` means your user home folder on Windows, macOS or Linux. In a remote VS Code window,
installation happens on the extension host, so use an assistant running on that same host.
Refresh or restart the assistant if new skills do not appear. Run the command again after a
Semanticus upgrade. Files you customised are kept, and the install result names those files.

On an older VS Code version, update VS Code or use the portable folders below in `.github/skills/`.
Installing both a plugin and standalone copies can show duplicate skills; choose one route per assistant.

## What is included

| Skill | Purpose |
|---|---|
| `semanticus` | Understand the current session and choose tools for the task. |
| `semanticus-optimize-dax` | Measure and compare DAX candidates across relevant contexts. |
| `semanticus-ai-ready` | Improve business metadata and readiness findings. |
| `semanticus-interview-model` | Check business answers against independent expected results. |
| `semanticus-model-pr` | Review and apply a Change Plan. |
| `semanticus-distill-workflow` | Turn a completed task into a reusable workflow. |
| `semanticus-curate-knowledge` | Merge and correct model insights. |

Ask naturally: "Use Semanticus to explain this model", "Optimise this measure across customer
and product contexts", or "Save this process as a workflow". You can also select a skill in your
assistant's skills menu. Plugin installations add the plugin's command namespace.

## Download as a plugin or portable skills

[Download the 1.1.3 skills pack](https://github.com/tenfingerseddy/semanticus-vscode/releases/download/v1.1.3/semanticus-assistant-skills-1.1.3.zip).
Extract it into a permanent folder. Replace `PACK_PATH` with the extracted folder containing its README.

Claude Code:

```text
claude plugin marketplace add "PACK_PATH"
claude plugin install semanticus@semanticus
```

Codex:

```text
codex plugin marketplace add "PACK_PATH"
codex plugin install semanticus@semanticus
```

For another skills-compatible assistant, copy the folders under `plugins/semanticus/skills/` into
its documented skills directory. For project-only use, Claude Code reads `.claude/skills/`, Codex
reads `.agents/skills/`, and Copilot also reads `.github/skills/`.

To update a plugin, extract the new pack into that permanent folder, refresh the marketplace and
update Semanticus in the assistant's plugin manager. Pack versions match the Semanticus release.

## Connect the same live model

Keep Semanticus open in VS Code. **Connect AI Assistant** writes a `.mcp.json` entry containing
the engine command, arguments and workspace. Claude Code and clients using that format can read it.
For a different client, copy the same command and arguments into its MCP configuration:

- Copilot uses the `servers` map in `.vscode/mcp.json`; set the entry's `type` to `stdio`.
- Codex uses `[mcp_servers.semanticus]` in its MCP configuration, with `command` and `args`.

The pack provides guidance only. It does not create another connection, supply a licence or carry
AI-provider credentials. The editable model, live query target and publishing destination can differ;
the quick-start teaches the assistant to check the relevant target before acting.

[Claude Code skills](https://code.claude.com/docs/en/skills) ·
[Codex skills](https://learn.chatgpt.com/docs/build-skills) ·
[Copilot skills](https://code.visualstudio.com/docs/agent-customization/agent-skills)
