# Semanticus assistant skills 1.1.3

Seven guides for the Semanticus MCP connection: getting started, DAX optimisation, AI readiness, model interviews, Change Plans, reusable workflows and model knowledge.

## Install from Semanticus

In VS Code, run **Semanticus: Install or Update Assistant Skills** and choose your assistant. Claude Code and Codex receive user skills for all projects. Copilot skills are contributed by the extension itself in VS Code versions that support extension skills. Existing customised skill files are preserved. Run the command again after a Semanticus update.

## Install this download as a plugin

Extract this ZIP into a permanent folder. Use the folder containing this README as PACK_PATH below. Install either the plugin or the user skills, so your assistant does not discover duplicate copies.

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

Refresh or restart your assistant if the skills do not appear. To update a plugin, extract the new release into that folder, refresh the marketplace and update Semanticus in the assistant plugin manager.

## Other skills-compatible assistants

Copy the folders under plugins/semanticus/skills into the assistant's documented skills directory. For project-only use, Claude Code reads .claude/skills and Codex reads .agents/skills. Copilot also reads .github/skills.

## Connect the model

These guides use your existing Semanticus MCP server. Keep Semanticus open in VS Code. Connect AI Assistant writes .mcp.json for clients that use that format. For another client, use the same command and arguments in its MCP configuration. The skills do not create another engine, supply a licence, or add AI credentials.

Ask: "Use Semanticus to explain this model", "Optimise this measure across customer and product contexts", or "Save this process as a workflow". Skills load when relevant. Essential orientation also comes from MCP, so skills are optional.

[Full setup guide](https://semanticus.com.au/docs/assistant-skills)
