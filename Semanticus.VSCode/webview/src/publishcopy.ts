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
    return { line: `Editing ${model} · no publish destination yet · ${restore}`, resolved: true };
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

export type PublishEntryState = 'choose-destination' | 'review' | 'compare';

// What the Publishing card shows below its header. Pure, because the review card must never render without a
// resolved destination: a confirm with no target invites "Publish to No live target" and "Publish anyway with a
// reason" against nothing. No destination outranks every other state, so there is one action on offer.
// "What to publish" is the default view for a resolved destination: the embedded comparison, ticked and ready,
// with no separate link to reach it.
export function publishEntryState(input: { canPublish: boolean; publishOpen: boolean }): PublishEntryState {
  if (!input.canPublish) return 'choose-destination';
  if (input.publishOpen) return 'review';
  return 'compare';
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

// A rollback result is read from `applied` and `failedRefs`, never from the absence of an error: the engine returns
// applied=false with a note when the target already matches the point, and a committed rollback can still carry
// objects it could not restore. The line claims exactly what happened.
export type RollbackOutcome = { applied: boolean; failedRefs?: string[] | null; note?: string | null; error?: string | null };
export function rollbackResultLine(r: RollbackOutcome, target: string, when: string): string {
  if (r.error) return r.error;
  if (!r.applied) return r.note || `${target} already matches this restore point. Nothing to roll back.`;
  const failed = r.failedRefs ?? [];
  const base = `Restored ${target} to the point from ${when}`;
  if (failed.length > 0) {
    const names = failed.slice(0, 3).join(', ') + (failed.length > 3 ? ` and ${failed.length - 3} more` : '');
    return `${base}, except ${failed.length} object${failed.length === 1 ? '' : 's'} that could not be restored: ${names}. Review changes to see what differs now.`;
  }
  return `${base}. Your local model edits are unchanged; review changes to see what differs now.`;
}

// After a subset publish the grid is re-read. Only a fresh, successful comparison may claim that what is still shown is
// unpublished; when the re-read fails the write summary stays and the list is marked as possibly out of date.
export function publishedSubsetLine(input: { count: number; target: string; failed: number; refreshed: boolean }): string {
  const changes = input.count === 1 ? '1 change' : `${input.count} changes`;
  const head = `Published ${changes} to ${input.target}.`;
  const failed = input.failed > 0 ? ` ${input.failed} could not be published.` : '';
  if (!input.refreshed) return `${head}${failed} The comparison could not be refreshed, so the list below may be out of date. Run Compare again to see what remains.`;
  return `${head}${failed} The differences still shown are not yet published.`;
}

// The stage as a person writes it. The registry stores canonical ids, which read like a typo in a sentence
// ("to Contoso Sales · uat"). A label outside the standard four is somebody's own word, so it prints as typed.
// Display only: permission decisions still key off the raw label.
const PUBLISH_STAGE_NAMES: Record<string, string> = { local: 'Local', dev: 'Development', uat: 'UAT', prod: 'Production' };
export function publishStageName(label: string): string { return PUBLISH_STAGE_NAMES[label.trim().toLowerCase()] ?? label; }
