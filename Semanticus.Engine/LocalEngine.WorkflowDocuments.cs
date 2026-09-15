using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        // All in-process document writers, including wholesale save/delete, share this lock.
        private static readonly object WorkflowDocumentLock = new object();
        private static readonly UTF8Encoding WorkflowDocumentUtf8 = new UTF8Encoding(false, true);

        private static string ValidateWorkflowDocumentName(string name)
        {
            name = (name ?? "").Trim();
            if (!KebabName.IsMatch(name))
                throw new InvalidOperationException($"'{name}' is not a valid workflow name: kebab-case (e.g. 'my-workflow'); it becomes the filename.");
            return name;
        }

        private void GuardWorkflowDocumentContext(SessionContext context, string sessionId)
        {
            EnsureContextCurrent(context, "Workflow document");
            if (sessionId != null && !string.Equals(context.Session?.Id, sessionId, StringComparison.Ordinal))
                throw new InvalidOperationException("The model changed before this workflow document operation landed. Nothing was written.");
        }

        private static string WorkflowByteHash(byte[] bytes) =>
            "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        // A UTF-8 BOM is part of ExactText and the byte hash. Only the parser's view removes it.
        private static WorkflowDef ParseWorkflowDocument(string text) =>
            WorkflowParser.Parse(text != null && text.Length > 0 && text[0] == (char)0xfeff ? text.Substring(1) : text);

        private WorkflowDocumentResult ReadWorkflowDocument(string name, string userDir, string stockDir)
        {
            if (!ResolveWorkflowLibrary(userDir, stockDir).Files.TryGetValue(name, out var resolved))
                throw new InvalidOperationException($"Workflow '{name}' not found (list_workflows shows the library).");
            return DescribeWorkflowDocument(name, resolved.Source, resolved.Path, File.ReadAllBytes(resolved.Path));
        }

        private static WorkflowDocumentResult DescribeWorkflowDocument(string name, string library, string file, byte[] bytes)
        {
            string text;
            try { text = WorkflowDocumentUtf8.GetString(bytes); }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidOperationException($"Workflow '{name}' is not valid UTF-8. Its bytes were not changed. Repair the encoding outside this editor before retrying.", ex);
            }
            var def = ParseWorkflowDocument(text);
            var error = def.Error;
            if (error == null && !string.Equals(def.Name, name, StringComparison.Ordinal))
                error = $"frontmatter name '{def.Name}' must equal the workflow name '{name}' (it is the file identity).";
            var result = new WorkflowDocumentResult
            {
                Name = name, Library = library, Path = Path.GetFullPath(file), ExactText = text, ByteHash = WorkflowByteHash(bytes),
                Metadata = new WorkflowDocumentMetadata
                {
                    SchemaVersion = def.SchemaVersion, Title = def.Title, Version = def.Version,
                    StepIds = def.Steps.Select(s => s.Id).ToArray(),
                    ExplicitIds = def.Steps.Length > 0 && def.Steps.All(s => s.HasExplicitId),
                    Parses = error == null, ParseError = error,
                },
            };
            if (error == null) result.EditModel = WorkflowDocumentPatcher.Project(text, def);
            return result;
        }

        public async Task<WorkflowDocumentResult> GetWorkflowDocumentAsync(string name, string sessionId = null)
        {
            RequireProFeature();
            name = ValidateWorkflowDocumentName(name);
            var context = _sessions.CurrentContext;
            var (userDir, stockDir) = WorkflowDirs(context);
            await context.WorkflowGate.WaitAsync().ConfigureAwait(false);
            try
            {
                GuardWorkflowDocumentContext(context, sessionId);
                lock (WorkflowDocumentLock) return ReadWorkflowDocument(name, userDir, stockDir);
            }
            finally { context.WorkflowGate.Release(); }
        }

        public async Task<WorkflowEditPreviewResult> PreviewWorkflowEditAsync(string name, string expectByteHash,
            string expectPath, string editsJson, string draftText = null, bool create = false, string sessionId = null)
        {
            RequireProFeature();
            name = ValidateWorkflowDocumentName(name);
            var context = _sessions.CurrentContext;
            var (userDir, stockDir) = WorkflowDirs(context);
            WorkflowDocumentResult current = null;
            string source;
            await context.WorkflowGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!WorkflowPreviewContextIsCurrent(context, sessionId))
                    return PreviewRefusal(name, "conflict", "The model changed before this workflow preview landed. Keep your draft and retry against the current model.",
                        null, draftText, "stale_session");
                lock (WorkflowDocumentLock)
                {
                    if (create)
                    {
                        if (!string.IsNullOrWhiteSpace(expectPath) || !string.IsNullOrWhiteSpace(expectByteHash))
                            return PreviewRefusal(name, "conflict", "A create preview does not take a saved path or byte hash.", null,
                                draftText, "create_identity");
                        if (userDir == null)
                            return PreviewRefusal(name, "conflict", "No project is open to hold a new workflow.", null,
                                draftText, "no_project");
                        if (File.Exists(Path.Combine(userDir, name + ".md")))
                        {
                            current = ReadWorkflowDocument(name, userDir, stockDir);
                            return PreviewRefusal(name, "conflict", "A project workflow with this name already exists. Nothing was written.",
                                current, draftText, "create_collision");
                        }
                        source = draftText ?? EmptyWorkflowDraft(name);
                    }
                    else
                    {
                        current = ReadWorkflowDocument(name, userDir, stockDir);
                        source = draftText ?? current.ExactText;
                        if (string.IsNullOrWhiteSpace(expectPath))
                            return PreviewRefusal(name, "conflict", "expectPath is required from the saved workflow document.", current, source, "stale_path");
                        if (!string.Equals(expectPath, current.Path, StringComparison.Ordinal))
                            return PreviewRefusal(name, "conflict", "The workflow now resolves to a different file. Keep your draft and review the current saved file.", current, source, "stale_path");
                        if (string.IsNullOrWhiteSpace(expectByteHash))
                            return PreviewRefusal(name, "conflict", "expectByteHash is required from the saved workflow document.", current, source, "stale_content");
                        if (!string.Equals(expectByteHash, current.ByteHash, StringComparison.Ordinal))
                            return PreviewRefusal(name, "conflict", "The workflow changed on disk. Keep your draft and reconcile it with the current saved file.", current, source, "stale_content");
                    }
                }
            }
            finally { context.WorkflowGate.Release(); }

            var patch = WorkflowDocumentPatcher.Apply(name, source, editsJson, create);
            if (!WorkflowPreviewContextIsCurrent(context, sessionId))
                return PreviewRefusal(name, "conflict", "The model changed while this workflow preview was being prepared. Keep your draft and retry against the current model.",
                    null, source, "stale_session");
            if (patch.ErrorCode != null)
            {
                var outcome = patch.ErrorCode is "unpreservable_spelling" or "source_map_mismatch"
                    or "ambiguous_duplicate" or "template_source_only" ? "unpreservable" : "invalid";
                var issue = new WorkflowEditIssue
                {
                    Code = patch.ErrorCode, Severity = "error", Target = patch.Target ?? "workflow",
                    Field = patch.Field, Message = patch.Error,
                };
                return new WorkflowEditPreviewResult
                {
                    Name = name, Outcome = outcome, CanApply = false, Reason = patch.Error,
                    Document = current, ProposedText = patch.Text,
                    ProposedByteHash = TryWorkflowByteHash(patch.Text),
                    Diff = WorkflowDocumentPatcher.Diff(name, source, patch.Text),
                    EditModel = patch.Model, Issues = new[] { issue }, KeyChanges = patch.KeyChanges.ToArray(),
                };
            }

            byte[] bytes;
            try { bytes = WorkflowDocumentUtf8.GetBytes(patch.Text); }
            catch (EncoderFallbackException)
            {
                return PreviewRefusal(name, "invalid", "The proposed draft is not valid Unicode for UTF-8. Nothing was written.",
                    current, patch.Text, "invalid_unicode");
            }
            var hash = WorkflowByteHash(bytes);
            var before = current?.ExactText ?? source;
            var noChange = current != null && string.Equals(hash, current.ByteHash, StringComparison.Ordinal)
                || current == null && string.Equals(patch.Text, source, StringComparison.Ordinal) && string.Equals(source, EmptyWorkflowDraft(name), StringComparison.Ordinal);
            if (noChange)
                return new WorkflowEditPreviewResult
                {
                    Name = name, Outcome = "no_change", CanApply = false,
                    Reason = "The requested edit already has these exact bytes. Nothing was written.",
                    Document = current, ProposedText = patch.Text, ProposedByteHash = hash, Diff = "",
                    EditModel = patch.Model, KeyChanges = patch.KeyChanges.ToArray(),
                };

            var candidate = ParseWorkflowDocument(patch.Text);
            candidate.Source = create ? "user" : current.Library;
            candidate.FilePath = create ? Path.Combine(userDir, name + ".md") : current.Path;
            var library = LoadWorkflowDefs();
            library.RemoveAll(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
            library.Add(candidate);
            var check = await CheckWorkflowDefAsync(candidate, library).ConfigureAwait(false);
            var issues = check.Findings.Select(f => new WorkflowEditIssue
            {
                Code = "admission_" + f.Severity, Severity = f.Severity == "warn" ? "warning" : f.Severity,
                Target = "workflow", Message = f.Message,
            }).ToArray();
            var warnings = issues.Any(i => i.Severity == "warning");
            await context.WorkflowGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!WorkflowPreviewContextIsCurrent(context, sessionId))
                    return PreviewRefusal(name, "conflict", "The model changed while this workflow preview was being prepared. Keep your draft and retry against the current model.",
                        null, source, "stale_session");
            }
            finally { context.WorkflowGate.Release(); }
            var stock = !create && current.Library != "user";
            var result = new WorkflowEditPreviewResult
            {
                Name = name, Outcome = stock ? "stock_read_only" : "ready", CanApply = !stock,
                RequiresReview = warnings,
                Reason = stock
                    ? "Stock workflows are read-only. Create an exact project copy before applying this preview."
                    : warnings ? "The draft is valid and has warnings to review before saving."
                    : "The draft is valid and ready to save through the existing exact-text writer.",
                Document = current, ProposedText = patch.Text, ProposedByteHash = hash,
                Diff = WorkflowDocumentPatcher.Diff(name, before, patch.Text), EditModel = patch.Model,
                Issues = issues, KeyChanges = patch.KeyChanges.ToArray(),
            };
            if (!stock)
                result.SuggestedNextAction = create
                    ? new WorkflowEditNextAction
                    {
                        Op = "save_workflow", Why = "Create this reviewed draft without replacing an existing project file.",
                        Args = new WorkflowEditApplyArgs { Name = name, Markdown = patch.Text, CreateOnly = true, SessionId = sessionId },
                    }
                    : new WorkflowEditNextAction
                    {
                        Op = "edit_workflow_document", Why = "Apply these reviewed exact bytes through the saved path and hash fence.",
                        Args = new WorkflowEditApplyArgs
                        {
                            Name = name, ExpectByteHash = current.ByteHash, ExpectPath = current.Path,
                            ExactText = patch.Text, SessionId = sessionId,
                        },
                    };
            return result;
        }

        private bool WorkflowPreviewContextIsCurrent(SessionContext context, string sessionId) =>
            ReferenceEquals(_sessions.CurrentContext, context)
            && (sessionId == null || string.Equals(context.Session?.Id, sessionId, StringComparison.Ordinal));

        private static WorkflowEditPreviewResult PreviewRefusal(string name, string outcome, string reason,
            WorkflowDocumentResult current, string draft, string code) => new WorkflowEditPreviewResult
        {
            Name = name, Outcome = outcome, CanApply = false, Reason = reason, Document = current,
            ProposedText = draft, ProposedByteHash = TryWorkflowByteHash(draft), Diff = "",
            Issues = new[] { new WorkflowEditIssue { Code = code, Severity = "error", Target = "workflow", Message = reason } },
        };

        private static string TryWorkflowByteHash(string text)
        {
            if (text == null) return null;
            try { return WorkflowByteHash(WorkflowDocumentUtf8.GetBytes(text)); }
            catch (EncoderFallbackException) { return null; }
        }

        private static string EmptyWorkflowDraft(string name)
        {
            var lf = ((char)10).ToString();
            return "---" + lf + "schemaVersion: 2" + lf + "name: " + name + lf
                + "title: New workflow" + lf + "version: 1" + lf + "---" + lf;
        }

        public async Task<WorkflowDocumentEditResult> EditWorkflowDocumentAsync(string name, string expectByteHash,
            string exactText, string expectPath, string origin, string sessionId = null)
        {
            RequireProFeature();
            name = ValidateWorkflowDocumentName(name);
            var context = _sessions.CurrentContext;
            var (userDir, stockDir) = WorkflowDirs(context);
            WorkflowDocumentEditResult result;
            await context.WorkflowGate.WaitAsync().ConfigureAwait(false);
            try
            {
                GuardWorkflowDocumentContext(context, sessionId);
                lock (WorkflowDocumentLock)
                {
                    var current = ReadWorkflowDocument(name, userDir, stockDir);
                    WorkflowDocumentEditResult Refuse(string reason) => new WorkflowDocumentEditResult
                    {
                        Name = name, Changed = false, Reason = reason, ByteHash = current.ByteHash, Document = current,
                    };
                    // A byte hash identifies content, not its owning project. Save As can move the same
                    // session to another root containing an identical file between the read and this call.
                    if (string.IsNullOrWhiteSpace(expectPath))
                        return Refuse("expectPath is required. Read get_workflow_document, then retry with its path.");
                    if (!string.Equals(expectPath, current.Path, StringComparison.Ordinal))
                        return Refuse("The workflow now resolves to a different file. Nothing was written. Read the current document before editing it.");
                    if (string.IsNullOrWhiteSpace(expectByteHash))
                        return Refuse("expectByteHash is required. Read get_workflow_document, then retry with its byteHash.");
                    if (!string.Equals(expectByteHash, current.ByteHash, StringComparison.Ordinal))
                        return Refuse("The workflow changed on disk. Read the current document and reconcile your draft before retrying. Current byteHash: " + current.ByteHash);
                    if (current.Library != "user")
                        return Refuse("Stock workflows are read-only. Create a project copy first.");
                    if (exactText == null) return Refuse("exactText is required. Nothing was written.");
                    byte[] bytes;
                    try { bytes = WorkflowDocumentUtf8.GetBytes(exactText); }
                    catch (EncoderFallbackException) { return Refuse("exactText is not valid Unicode for UTF-8. Nothing was written."); }
                    if (WorkflowByteHash(bytes) == current.ByteHash) return Refuse("The document already has these exact bytes. Nothing was written.");
                    var proposed = DescribeWorkflowDocument(name, "user", current.Path, bytes);
                    if (!proposed.Metadata.Parses) return Refuse("The workflow does not parse. Nothing was written. " + proposed.Metadata.ParseError);
                    var temp = WriteWorkflowDocumentTemp(current.Path, bytes);
                    try
                    {
                        // External editors do not take our lock. Catch changes during temp-file preparation;
                        // a small external-writer race remains between this last reread and atomic replace.
                        current = ReadWorkflowDocument(name, userDir, stockDir);
                        if (current.Library != "user" || current.Path != expectPath || current.ByteHash != expectByteHash)
                            return Refuse("The workflow changed on disk. Nothing was written. Current byteHash: " + current.ByteHash);
                        File.Move(temp, current.Path, true);
                    }
                    finally { if (File.Exists(temp)) File.Delete(temp); }
                    result = new WorkflowDocumentEditResult
                    {
                        Name = name, Changed = true, Reason = "Saved the workflow document.", ByteHash = proposed.ByteHash, Document = proposed,
                    };
                }
            }
            finally { context.WorkflowGate.Release(); }
            await PublishWorkflowLibraryAsync();
            return result;
        }

        private static string WriteWorkflowDocumentTemp(string file, byte[] bytes)
        {
            var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                stream.Write(bytes);
                stream.Flush(true);
                return temp;
            }
            catch { if (File.Exists(temp)) File.Delete(temp); throw; }
        }
    }
}
