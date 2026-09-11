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

        private static WorkflowDocumentResult ReadWorkflowDocument(string name, string userDir, string stockDir)
        {
            var userFile = userDir == null ? null : Path.Combine(userDir, name + ".md");
            var library = userFile != null && File.Exists(userFile) ? "user" : "stock";
            var file = library == "user" ? userFile : Path.Combine(stockDir, name + ".md");
            if (!File.Exists(file))
                throw new InvalidOperationException($"Workflow '{name}' not found (list_workflows shows the library).");
            return DescribeWorkflowDocument(name, library, file, File.ReadAllBytes(file));
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
            return new WorkflowDocumentResult
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
        }

        public async Task<WorkflowDocumentResult> GetWorkflowDocumentAsync(string name, string sessionId = null)
        {
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

        public async Task<WorkflowDocumentEditResult> EditWorkflowDocumentAsync(string name, string expectByteHash,
            string exactText, string expectPath, string origin, string sessionId = null)
        {
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
