// One word for the live write: Publish. Pure helpers so the status chip, the Ship page, and the confirm
// card cannot drift, and so the UAT defects have host-free tests.

export const PUBLISH_COMMAND_TITLE = 'Semanticus: Publish to Live Model';
export const PUBLISH_TREE_TOOLTIP = 'Publish to the live model';
export const FABRIC_GIT_EMPTY_ID = 'Enter a workspace id first.';
export const UNKNOWN_ACCOUNT = 'As the account this model was opened with';

export type PublishChipInput = {
  liveBound: boolean;
  changeCount?: number | null;
  liveOnlyCount?: number | null;
  previewError?: string | null;
};

/** Status-bar chip text. Null means hide the chip (the model is not live). */
export function publishChipText(input: PublishChipInput): string | null {
  if (!input.liveBound) return null;
  if (input.previewError) return 'Publish · could not check';
  if (input.changeCount == null) return 'Publish';
  const liveOnly = input.liveOnlyCount ?? 0;
  if (input.changeCount === 0 && liveOnly > 0) {
    return liveOnly === 1 ? 'Publish · 1 to remove' : `Publish · ${liveOnly} to remove`;
  }
  if (input.changeCount === 0) return 'Publish · up to date';
  return input.changeCount === 1 ? 'Publish · 1 change' : `Publish · ${input.changeCount} changes`;
}

export type DeployHeaderInput = {
  loading: boolean;
  liveBound: boolean;
  modelName: string;
  targetName: string;
  changeCount?: number | null;
  previewError?: string | null;
  lastRestore: string;
};

export type DeployHeaderState = { line: string; resolved: boolean };

/** Ship > Deploy header. Must resolve after the first load; it must never stay on a loading sentence. */
export function deployHeaderState(input: DeployHeaderInput): DeployHeaderState {
  const model = input.modelName || 'the open model';
  const restore = `last restore point: ${input.lastRestore}`;
  if (input.loading) {
    return { line: `Editing ${model} · checking for changes · ${restore}`, resolved: false };
  }
  if (!input.liveBound) {
    return { line: `Editing ${model} · not connected to a live model · ${restore}`, resolved: true };
  }
  if (input.previewError) {
    return { line: `Editing ${model} · publishing to ${input.targetName} · could not check for changes · ${restore}`, resolved: true };
  }
  if (input.changeCount == null) {
    return { line: `Editing ${model} · publishing to ${input.targetName} · click Publish to review changes · ${restore}`, resolved: true };
  }
  const waiting = input.changeCount === 0
    ? 'nothing to publish'
    : input.changeCount === 1 ? '1 change waiting' : `${input.changeCount} changes waiting`;
  return { line: `Editing ${model} · publishing to ${input.targetName} · ${waiting} · ${restore}`, resolved: true };
}

export function publishAccountLine(account?: string | null): string {
  const name = (account || '').trim();
  return name ? `As ${name}` : UNKNOWN_ACCOUNT;
}

export function publishButtonLabel(changeCount: number, targetName: string, deleteCount = 0): string {
  // No copy invites a no-op write. When there is nothing to publish the confirm control is disabled anyway, but the
  // label must not read as an invitation to write ("Publish 0 changes to V2").
  if (changeCount <= 0 && deleteCount === 0) return 'Nothing to publish';
  const target = targetName || 'the live model';
  const changes = changeCount === 1 ? '1 change' : `${Math.max(0, changeCount)} changes`;
  if (deleteCount > 0) {
    const removed = deleteCount === 1 ? '1' : String(deleteCount);
    return `Publish ${changes} and remove ${removed} from ${target}`;
  }
  return `Publish ${changes} to ${target}`;
}

export function fabricGitWorkspaceMessage(workspaceId: string): string | null {
  return workspaceId.trim() ? null : FABRIC_GIT_EMPTY_ID;
}

export function shouldResetCompareOnSessionChange(previousSessionId?: string | null, nextSessionId?: string | null): boolean {
  return !!previousSessionId && !!nextSessionId && previousSessionId !== nextSessionId;
}

export function nothingToPublishCopy(targetName: string, liveOnly: string[] = []): string {
  const target = targetName || 'the live model';
  if (liveOnly.length === 0) return `Nothing to publish. ${target} already has everything in your copy.`;
  const extra = liveOnly.length === 1
    ? `1 object is on ${target} that your copy does not have: ${liveOnly[0]}.`
    : `${liveOnly.length} objects are on ${target} that your copy does not have.`;
  return `Nothing new to publish. ${extra} Publishing never removes them unless you tick them.`;
}

export function previewFailedCopy(targetName: string, error: string): string {
  const target = targetName || 'the live model';
  const reason = (error || '').trim() || 'the check did not finish';
  return `Could not check ${target} for changes: ${reason}. Sign in and try again.`;
}

// The honest in-flight state: while the change check is still running there is no answer yet, so the summary must
// never claim "Nothing to publish" (the three-second flicker where CHECKS reads "Checking this change" but the
// summary already declared the target up to date).
export function checkingCopy(targetName: string): string {
  const target = targetName || 'the live model';
  return `Checking ${target} for changes.`;
}
