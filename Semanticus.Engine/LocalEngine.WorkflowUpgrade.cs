using System;
using System.IO;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        public async Task<WorkflowUpgradeResult> UpgradeWorkflowAsync(string name, bool dryRun = true,
            string expectByteHash = null, string expectPath = null, string origin = "human", string sessionId = null)
        {
            RequireProFeature();
            name = ValidateWorkflowDocumentName(name);
            var context = _sessions.CurrentContext;
            var (userDir, stockDir) = WorkflowDirs(context);
            WorkflowUpgradeResult result;
            await context.WorkflowGate.WaitAsync().ConfigureAwait(false);
            try
            {
                GuardWorkflowDocumentContext(context, sessionId);
                lock (WorkflowDocumentLock)
                {
                    var current = ReadWorkflowDocument(name, userDir, stockDir);
                    WorkflowUpgradeResult Refuse(string reason) => new WorkflowUpgradeResult
                    {
                        Name = name, DryRun = dryRun, Reason = reason, Document = current,
                        ProposedText = current.ExactText, Diff = "",
                    };
                    if (expectPath != null && !string.Equals(expectPath, current.Path, StringComparison.Ordinal))
                        return Refuse("The workflow now resolves to a different file. Preview the current document before applying an upgrade. Nothing was written.");
                    if (expectByteHash != null && !string.Equals(expectByteHash, current.ByteHash, StringComparison.Ordinal))
                        return Refuse("The workflow changed on disk. Preview the current document before applying an upgrade. Nothing was written. Current byteHash: " + current.ByteHash);
                    if (!dryRun && current.Library != "user")
                        return Refuse("Stock workflows are read-only. Create a project copy, then preview that copy. Nothing was written.");
                    if (!dryRun && string.IsNullOrWhiteSpace(expectPath))
                        return Refuse("Applying an upgrade requires expectPath from the reviewed preview. Nothing was written.");
                    if (!dryRun && string.IsNullOrWhiteSpace(expectByteHash))
                        return Refuse("Applying an upgrade requires expectByteHash from the reviewed preview. Nothing was written.");

                    result = WorkflowUpgrade.Preview(current, dryRun);
                    if (dryRun && result.CanApply)
                        result.SuggestedNextAction = new WorkflowUpgradeNextAction
                        {
                            Op = "upgrade_workflow",
                            Args = new WorkflowUpgradeApplyArgs
                            {
                                Name = name, DryRun = false, ExpectByteHash = current.ByteHash,
                                ExpectPath = current.Path, SessionId = context.Session?.Id,
                            },
                            Why = "Review the proposed diff, then apply the upgrade to this saved version.",
                        };
                    if (dryRun || !result.CanApply) return result;
                    var bytes = WorkflowDocumentUtf8.GetBytes(result.ProposedText);
                    var temp = WriteWorkflowDocumentTemp(current.Path, bytes);
                    try
                    {
                        // An outside editor does not take our lock; the final reread narrows but cannot remove
                        // the external-writer race between this comparison and the atomic replacement.
                        current = ReadWorkflowDocument(name, userDir, stockDir);
                        if (current.Library != "user" || current.Path != expectPath || current.ByteHash != expectByteHash)
                            return Refuse("The workflow changed on disk. Nothing was written. Preview the current document again. Current byteHash: " + current.ByteHash);
                        File.Move(temp, current.Path, true);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                    result.Document = DescribeWorkflowDocument(name, "user", current.Path, bytes);
                    result.Changed = true;
                    result.CanApply = false;
                    result.Reason = "Upgraded the workflow to version 2. Existing instructions and gates are preserved.";
                }
                _sessions.Bus.PublishActivity(new ActivityEvent
                {
                    Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin,
                    Kind = "upgrade_workflow", Target = name, Label = $"Upgraded workflow '{name}' to version 2", Ok = true,
                });
            }
            finally { context.WorkflowGate.Release(); }
            await PublishWorkflowLibraryAsync();
            return result;
        }
    }
}
