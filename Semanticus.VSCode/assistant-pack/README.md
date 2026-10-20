# Semanticus assistant skills 1.6.0

Seven guides for the Semanticus MCP connection: getting started, DAX optimisation, AI readiness, model interviews, change lists, reusable workflows and model knowledge.

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
codex plugin add semanticus@semanticus
```

Refresh or restart your assistant if the skills do not appear. To update a plugin, extract the new release into that folder, refresh the marketplace and update Semanticus in the assistant plugin manager.

## Other skills-compatible assistants

Copy the folders under plugins/semanticus/skills into the assistant's documented skills directory. For project-only use, Claude Code reads .claude/skills and Codex reads .agents/skills. Copilot also reads .github/skills.

## Connect the model

These guides use your existing Semanticus MCP server. Keep Semanticus open in VS Code. Connect AI Assistant writes .mcp.json for clients that use that format. For another client, use the same command and arguments in its MCP configuration. The skills do not create another engine, supply a licence, or add AI credentials.

The workbench has six areas: Plan, Explore, Edit, Review, Publish and Workflows. The Plan is where you start, and its History records every edit and who made it. The areas are navigation labels for people; MCP tool names do not depend on them. The VS Code view updates when an edit lands, and your assistant sees it on its next call. Keep the editing model, test model and publishing destination distinct because they can differ.

Apply changes the working model as one undoable edit. Save keeps local work or writes a local workflow or spec. Publish sends reviewed model definitions to a chosen live destination after preview and confirmation. Restore returns a live destination to an earlier published version. Publish and Restore do not refresh data. A publish keeps a restore point: Undo publish on the Publish page returns the published model to it, and `undo_change` only reverses edits to the working copy.

For workflow authoring, read get_workflow_document, preview typed changes with preview_workflow_edit, then write a reviewed existing file with edit_workflow_document or create a new project file with save_workflow. Starters, the workflows that come with Semanticus, are read-only; instantiate_workflow_template copies one into the project as your workflow. Use get_workflow_layout and save_workflow_layout for Canvas positions only.

Ask: "Use Semanticus to explain this model", "Optimise this measure across customer and product contexts", or "Save this process as a workflow". Skills load when relevant. Essential orientation also comes from MCP, so skills are optional.

[Full setup guide](https://semanticus.com.au/docs/assistant-skills)
