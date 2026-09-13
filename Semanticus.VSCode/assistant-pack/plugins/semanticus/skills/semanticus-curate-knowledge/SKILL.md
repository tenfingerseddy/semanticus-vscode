---
name: semanticus-curate-knowledge
description: Review and tidy Semanticus model knowledge by merging duplicate insights and correcting outdated lessons. Use when asked to clean up insights or maintain the model's learning store.
---

# Maintain model knowledge

Read `list_insights` for the requested project or global scope. Inspect provenance, content and
use counters; pending insights are separate from approved knowledge. Read relevant Primer
sections when deciding whether a lesson still fits.

Merge true duplicates into a clear survivor with `edit_insight`, retaining useful keys and source
references. Remove the superseded duplicate with `delete_insight` when the requested cleanup
includes removal. Correct outdated lessons when their useful meaning can be preserved.

Use `upvote_insight` or `downvote_insight` when actual outcomes support it. A low retrieval count
alone does not prove a lesson wrong or useless. Do not schedule maintenance or change global
knowledge merely because a project cleanup was requested.

Use these tools to preserve append-only history. Do not rewrite knowledge JSONL files by hand.
Review Primer suggestions through the Primer tools. Existing user authorization determines which
proposals can be accepted. Report merges, corrections, retirements and unresolved evidence conflicts.
