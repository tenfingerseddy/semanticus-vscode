---
name: adopt-source-control
title: Put the model under source control
description: Get the model into version control the officially-recommended way: text-based TMDL on disk, a repo target chosen deliberately (local git, Azure Repos, GitHub, or Fabric Git), and a review-the-diff-before-you-commit working loop, then gate on a clean, pushed first commit before the deploy pipeline can build on it.
version: 1
strictness: hard
triggers: [git_status, fabric_git_connect, cicd_generate]
---

## Step 1: Get to a text-based, source-controllable posture

Source control starts with the file format: **PBIP over .pbix** and **TMDL over
.bim**, officially recommended precisely because human-readable metadata is what enables source control and
conflict-free merging in the first place. Semanticus's on-disk format already IS TMDL, so the move is simply
to `save_model` to a folder (not a packed .pbix), which lays the model out as diffable text files. Read a
representative object back with `get_object` to confirm the definition is on disk as expected before you wrap
git around it.

```yaml gate
ops: [save_model, get_object]
```

## Step 2: Choose the repo target and wire it

Version control is recommended for ALL content: for merge/conflict handling, change identification, and
rollback. Decide the target first, then wire it. `git_clone` an existing repo or point
at a local one, and drive the working git ops (`git_status`, `git_branch`, `git_commit`, `git_push`) for the
local / Azure Repos / GitHub path. **Fabric Git integration is a valid third path** alongside the OneDrive →
Azure Repos simple-to-advanced spectrum: `fabric_git_connect` binds a workspace to a repo and `fabric_git_status`
shows the workspace ⇄ git diff. Pick the path that matches where this model lives and wire exactly that one.

```yaml gate
ops: [git_clone, git_status, git_branch, git_commit, git_push, fabric_git_connect, fabric_git_status]
inputs:
  - name: repoTarget
    question: "Where is this model's source control: local git, Azure Repos, GitHub, or Fabric Git integration (workspace-connected)?"
    type: text
    required: required
```

## Step 3: Establish the working loop: review the diff, then commit

Adopt the habit that makes source control worth having: review changes as a diff BEFORE committing. Run
`model_diff` for the SEMANTIC view of what changed (measures, columns, relationships) and `git_status` +
`git diff` for the TEXT view of which TMDL files moved; the two are complementary, and the semantic diff
catches intent that a text diff buries. Then commit small and message the WHY, not the what, with
`git_commit`. Small, reasoned commits are what make change identification and rollback actually usable later.

```yaml gate
ops: [git_status, model_diff, git_commit]
inputs:
  - name: diffReviewed
    question: "Before committing, did you review both the semantic diff (model_diff) and the file diff (git_status/git diff), and does the commit message capture WHY the change was made?"
    type: text
    required: answer-or-decline
```

## Step 4: Gate on a clean first commit

The whole point is a definition that is committed and pushed, with nothing dangling. Confirm below that the
model's definition is committed AND pushed to the remote, and that `git_status` (or `fabric_git_status`) comes
back clean: no uncommitted worktree changes, no unsaved in-memory edits. Until that is true, source control
is aspirational, not real.

```yaml gate
inputs:
  - name: firstCommitDone
    question: "Is the model's definition committed AND pushed to the remote, with a clean git_status (no unsaved edits, no uncommitted changes)?"
    type: text
    required: answer-or-decline
```

## Step 5: What this unlocks

With the model under version control, the validate to deploy pipeline has something to build on: every
promotion diffs against a committed baseline, `cicd_generate` can scaffold the CI/CD pipeline from the repo,
and rollback is a git operation rather than a heroic recovery. Hand off to `deploy-to-production` when you are
ready to promote; it treats the deploy like a pull request against exactly the history you just established
here.

```yaml gate
ops: [cicd_generate]
```
