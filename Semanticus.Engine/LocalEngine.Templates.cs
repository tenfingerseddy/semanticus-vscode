using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using TabularEditor.TOMWrapper;

namespace Semanticus.Engine
{
    /// <summary>
    /// Workflow TEMPLATES (docs/pro-mode-spec.md §10) — the customisation layer. A template is a workflow-shaped
    /// markdown file with a <c>kind: template</c> frontmatter and a <c>slots:</c> declaration list; the body
    /// references each slot as <c>{{name}}</c>. Templates live in their OWN dirs (stock beside the binary,
    /// user under .semanticus/workflow-templates) so the workflow loader, seed counts, and list_workflows are
    /// untouched — a shelf is not the board. They are never runnable: instantiate_workflow_template renders one
    /// into a concrete workflow through a DETERMINISTIC, STRUCTURE-PRESERVING, admission-gated pipeline (§10.4).
    /// All five belong to Workflows, which is a Pro feature as a whole, template reads included (Kane 2026-09-15).
    /// </summary>
    public sealed partial class LocalEngine
    {
        private (string userDir, string stockDir) TemplateDirs()
        {
            var sidecar = SidecarDir();
            return (sidecar == null ? null : Path.Combine(sidecar, "workflow-templates"),
                    Path.Combine(AppContext.BaseDirectory, "workflow-templates"));
        }

        /// <summary>Load stock + user templates (a user file shadows a stock one of the same name, copy-to-customise
        /// — the workflows rule). A file in the templates dir that ISN'T a template is surfaced with an Error, never
        /// silently run — the mirror of LoadWorkflowDefs's placement guard.</summary>
        private List<WorkflowDef> LoadTemplateDefs()
        {
            var (userDir, stockDir) = TemplateDirs();
            var defs = WorkflowParser.LoadDirectory(stockDir, "stock");
            foreach (var user in WorkflowParser.LoadDirectory(userDir, "user"))
            {
                defs.RemoveAll(d => string.Equals(d.Name, user.Name, StringComparison.Ordinal)); // user shadows stock
                defs.Add(user);
            }
            foreach (var d in defs)
                if (d.Error == null && !string.Equals(d.Kind, "template", StringComparison.Ordinal))
                    d.Error = "this file lives in workflow-templates/ but is missing 'kind: template'. Add it to make this a template, or move the file to workflows/ to make it a runnable workflow.";
            return defs;
        }

        // ---- the five ops (all FREE — authoring is content) --------------------------------------

        /// <summary>§10.5: the shelf — one summary row per template (name/title/whenToUse/version/source + a slot
        /// summary: count + names). Free, read-only.</summary>
        public Task<WorkflowTemplateInfo[]> ListWorkflowTemplatesAsync()
        {
            RequireProFeature();
            return ListWorkflowTemplatesCoreAsync();
        }

        // The ungated core: a Pro workflow-template write re-reads the shelf to return the new library, and must
        // not re-run the gate it already passed.
        private Task<WorkflowTemplateInfo[]> ListWorkflowTemplatesCoreAsync()
            => Task.FromResult(LoadTemplateDefs()
                .Select(d => new WorkflowTemplateInfo
                {
                    Name = d.Name, Title = d.Title, WhenToUse = d.WhenToUse, Version = d.Version, Source = d.Source,
                    SlotCount = d.Slots.Length, Slots = d.Slots.Select(s => s.Name).ToArray(), Error = d.Error,
                })
                .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToArray());

        /// <summary>§10.5: one template's full definition — the slot declarations to fill PLUS the raw markdown
        /// body (with {{slot}} references intact). Free, read-only.</summary>
        public Task<WorkflowTemplate> GetWorkflowTemplateAsync(string name)
        {
            RequireProFeature();
            var def = LoadTemplateDefs().FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Template '{name}' not found (list_workflow_templates shows the shelf).");
            return Task.FromResult(new WorkflowTemplate
            {
                Name = def.Name, Title = def.Title, Description = def.Description, WhenToUse = def.WhenToUse,
                Version = def.Version, Source = def.Source, Error = def.Error, Slots = def.Slots,
                Markdown = def.FilePath != null && File.Exists(def.FilePath) ? File.ReadAllText(def.FilePath) : null,
            });
        }

        /// <summary>§10.5: author/edit a USER template — PARSE-VALIDATE FIRST (a file the parser refuses is never
        /// written). Refuses junk the way save_workflow does: not a template (no kind:template), a {{ref}} with no
        /// slot declaration, a slot with no example: (the trial instantiation renders with it). An UNUSED slot is
        /// NOT a save-blocker — check_workflow warns on it (an author may add a slot before wiring its {{ref}}).</summary>
        public async Task<WorkflowTemplateInfo[]> SaveWorkflowTemplateAsync(string name, string markdown, string origin)
        {
            GuardOutsideWrite(origin);
            RequireProFeature();
            name = (name ?? "").Trim();
            if (!KebabName.IsMatch(name))
                throw new InvalidOperationException($"'{name}' is not a valid template name: kebab-case (e.g. 'metric-certification'); it becomes the filename.");
            var def = WorkflowParser.Parse(markdown);
            if (def.Error != null)
                throw new InvalidOperationException($"The template does not parse. Nothing was written. {def.Error} Fix the reported error, then re-run save_workflow_template (or check_workflow to re-validate).");
            if (!string.Equals(def.Kind, "template", StringComparison.Ordinal))
                throw new InvalidOperationException("This is not a template: its frontmatter must declare 'kind: template'. A file without it is a runnable workflow; use save_workflow for that. Nothing was written.");
            if (!string.Equals(def.Name, name, StringComparison.Ordinal))
                throw new InvalidOperationException($"frontmatter name '{def.Name}' must equal the template name '{name}' (it is the file identity). Nothing was written.");

            var refs = SlotRefs(markdown);
            var declared = def.Slots.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            var undeclared = refs.Where(r => !declared.Contains(r)).OrderBy(r => r, StringComparer.Ordinal).ToArray();
            if (undeclared.Length > 0)
                throw new InvalidOperationException($"The body references {string.Join(", ", undeclared.Select(r => "{{" + r + "}}"))} with no matching slot declaration. Declare each in slots:, or fix the reference. Nothing was written.");
            var noExample = def.Slots.Where(s => string.IsNullOrWhiteSpace(s.Example)).Select(s => s.Name).ToArray();
            if (noExample.Length > 0)
                throw new InvalidOperationException($"Every slot needs an example: (the trial instantiation renders with it, and the Studio fill-form shows it). Missing on: {string.Join(", ", noExample)}. Nothing was written.");

            var (userDir, _) = TemplateDirs();
            if (userDir == null)
                throw new InvalidOperationException("No place to store user templates. Run open_model (or save_model after create_model) so the .semanticus sidecar has a home, or run the engine with a workspace.");
            RequireApprovedBasis(nameof(SaveWorkflowTemplateAsync), GateFileState(Path.Combine(userDir, name + ".md")), "template");
            Directory.CreateDirectory(userDir);
            await Task.Run(() => File.WriteAllText(Path.Combine(userDir, name + ".md"), markdown));
            return await ListWorkflowTemplatesCoreAsync();
        }

        /// <summary>§10.5: delete a USER template. Stock templates are read-only by construction (a customised
        /// shadow reverts to stock) — deleting a stock name without a user copy is refused instructively.</summary>
        public async Task<WorkflowTemplateInfo[]> DeleteWorkflowTemplateAsync(string name, string origin)
        {
            GuardOutsideWrite(origin);
            RequireProFeature();
            var (userDir, stockDir) = TemplateDirs();
            var trimmed = (name ?? "").Trim();
            var file = userDir == null ? null : Path.Combine(userDir, trimmed + ".md");
            if (file == null || !File.Exists(file))
                throw new InvalidOperationException(
                    File.Exists(Path.Combine(stockDir, trimmed + ".md"))
                        ? $"'{name}' is a stock template (read-only, shipped with the engine) and has no user copy to delete. Customised copies live in .semanticus/workflow-templates."
                        : $"User template '{name}' not found: list_workflow_templates shows the shelf, and only your own copies (under .semanticus/workflow-templates) are deletable.");
            // the file this delete removes, as it was when the person was asked (words a person wrote since are theirs)
            RequireApprovedBasis(nameof(DeleteWorkflowTemplateAsync), GateFileState(file), "template");
            await Task.Run(() => File.Delete(file));
            return await ListWorkflowTemplatesCoreAsync();
        }

        /// <summary>§10.4: render a template into a concrete workflow — the load-bearing algorithm.
        /// (a) validate newName + every required slot answered + type-check; (b) deterministic text substitution
        /// (no inference — golden rule 1: filling a template is sed, not an LLM call); (c) THE structure-preserving
        /// invariant (the injection defence — see CheckStructurePreserved); (d) the standard check_workflow-grade
        /// admission; (e) save through the workflow write path (library broadcast included). FREE.</summary>
        public async Task<WorkflowInfo[]> InstantiateWorkflowTemplateAsync(string templateName, string newName, string valuesJson, string origin)
        {
            GuardOutsideWrite(origin);
            RequireProFeature();
            var tmpl = LoadTemplateDefs().FirstOrDefault(d => string.Equals(d.Name, templateName, StringComparison.OrdinalIgnoreCase));
            if (tmpl == null) return await UseStockStarterAsync(templateName, newName, origin);
            if (tmpl.Error != null)
                throw new InvalidOperationException($"Template '{templateName}' does not parse and cannot be instantiated: {tmpl.Error}");
            var markdown = File.ReadAllText(tmpl.FilePath);
            RequireApprovedBasis(nameof(InstantiateWorkflowTemplateAsync), TemplateBasis(tmpl.FilePath, markdown), "template");

            // (a) newName kebab-case (blank takes the starter's own name) + no collision with an existing workflow,
            // and never a new file that would shadow a required workflow (contract 21.2).
            newName = string.IsNullOrWhiteSpace(newName) ? tmpl.Name : newName.Trim();
            if (!KebabName.IsMatch(newName))
                throw new InvalidOperationException($"'{newName}' is not a valid workflow name: kebab-case (e.g. 'fy26-metric-cert'); it becomes the new workflow's filename.");
            var (userDir, stockDir) = WorkflowDirs();
            if (WouldShadowRequiredWorkflow(newName, userDir, stockDir))
                throw new InvalidOperationException($"{RequiredShadowRefusal} Nothing was written.");
            if (LoadWorkflowDefs().Any(d => string.Equals(d.Name, newName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A workflow named '{newName}' already exists. Pick a different name (or delete_workflow to replace it). list_workflows shows the library.");

            // (a) collect + type-check the slot values; an absent optional slot takes its default.
            var provided = ParseSlotValues(valuesJson);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            var modelOpen = _sessions.Current != null;
            foreach (var slot in tmpl.Slots)
            {
                if (!provided.TryGetValue(slot.Name, out var v) || v == null)
                {
                    if (string.Equals(slot.Required, "required", StringComparison.Ordinal))
                        throw new InvalidOperationException(
                            $"Required slot '{slot.Name}' has no value. Add it to the values JSON. It asks: {slot.Question}"
                            + (string.IsNullOrWhiteSpace(slot.Example) ? "" : $" (example: {slot.Example})"));
                    values[slot.Name] = slot.Default ?? "";   // optional absent → default (or empty)
                    continue;
                }
                switch (slot.Type)
                {
                    case "enum":
                        if (slot.Values.Length > 0 && !slot.Values.Contains(v, StringComparer.Ordinal))
                            throw new InvalidOperationException($"Slot '{slot.Name}' = '{v}' is not one of the allowed values: {string.Join(", ", slot.Values)}.");
                        break;
                    case "number":
                        if (!double.TryParse(v, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out _))
                            throw new InvalidOperationException($"Slot '{slot.Name}' = '{v}' is not a number.");
                        break;
                    case "objectRef":
                        // Warn-only, and only when a model is open — a template is often filled before the model
                        // exists, and the instance's own gates re-resolve the ref at run time.
                        if (modelOpen && !await _sessions.Require().ReadAsync(m => ObjectRefs.Resolve(m, v) != null))
                            _sessions.Bus.PublishActivity(new ActivityEvent
                            {
                                Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin,
                                Kind = "instantiate_workflow_template", Ok = true, Target = newName,
                                Label = $"Slot '{slot.Name}' = '{v}' does not resolve to an object on the current model (warn: the instance's gates re-check at run time).",
                            });
                        break;
                    default: break;   // text | verification — free-form
                }
                values[slot.Name] = v;
            }

            // (c) the structure-preserving invariant — refuse BEFORE rendering so an injection never lands on disk.
            var (structOk, structDiff) = CheckStructurePreserved(markdown, tmpl, values);
            if (!structOk)
                throw new InvalidOperationException($"Instantiation refused: {structDiff}");

            // (b) render — deterministic substitution + provenance frontmatter.
            var slotValuesJson = JsonSerializer.Serialize(values);   // single-line by default (newlines are \n-escaped)
            var rendered = RenderInstance(markdown, newName, tmpl.Name, tmpl.Version, slotValuesJson, values, tmpl.SchemaVersion);

            // (d) admission — the same dry-run an authored workflow gets; refuse on warns (or a parse fault).
            var renderedDef = WorkflowParser.Parse(rendered);
            if (renderedDef.Error != null)
                throw new InvalidOperationException($"The rendered workflow does not parse. Nothing was saved. {renderedDef.Error}");
            var admission = await CheckWorkflowDefAsync(renderedDef);
            if (!admission.Ok)
            {
                var why = admission.ParseError
                    ?? string.Join(" | ", admission.Findings.Where(f => string.Equals(f.Severity, "warn", StringComparison.Ordinal)).Select(f => f.Message));
                throw new InvalidOperationException($"The rendered workflow is not admissible. Nothing was saved. {why} (check_workflow('{templateName}') validates the template itself.)");
            }

            // (e) save through the workflow write path (parse-validates again + broadcasts the library).
            // The library broadcast is the full set; this result is the workflow just created so the caller
            // does not have to hunt through the shelf.
            var library = await SaveWorkflowAsync(newName, InCurrentFormat(newName, rendered), origin);
            return library.Where(w => string.Equals(w.Name, newName, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        /// <summary>Contract 21.2 "Use" for a shipped workflow: the same op as a template, so one verb reaches both
        /// doors. A starter a team rule requires is returned as it is and never copied (a copy would win resolution
        /// and quietly replace it). Otherwise the shipped bytes are copied create-only into this model's workflows,
        /// under the starter's own name or <paramref name="newName"/> (only the frontmatter name changes).</summary>
        private async Task<WorkflowInfo[]> UseStockStarterAsync(string starterName, string newName, string origin)
        {
            var (userDir, stockDir) = WorkflowDirs();
            var stock = WorkflowFiles(stockDir);
            var required = RequiredWorkflowSet(userDir, stockDir);
            // A parked starter a team rule still requires is returned as it is, like any required starter.
            var parkedRequired = required.FirstOrDefault(r => string.Equals(r, (starterName ?? "").Trim(), StringComparison.OrdinalIgnoreCase)
                                                              && ParkedWorkflowSentence(r) != null && !stock.ContainsKey(r));
            if (parkedRequired != null)
                return (await BuildLibraryInfosAsync()).Where(w => string.Equals(w.Name, parkedRequired, StringComparison.Ordinal)).ToArray();
            var name = stock.Keys.FirstOrDefault(k => string.Equals(k, (starterName ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw WorkflowNotFound(starterName, $"'{starterName}' is not a starter or a template. list_workflows marks the starters, and list_workflow_templates shows the templates.");
            if (required.Contains(name))
                return (await BuildLibraryInfosAsync()).Where(w => string.Equals(w.Name, name, StringComparison.Ordinal)).ToArray();

            newName = string.IsNullOrWhiteSpace(newName) ? name : ValidateWorkflowDocumentName(newName);
            var markdown = File.ReadAllText(stock[name]);
            if (!string.Equals(newName, name, StringComparison.Ordinal)) markdown = WithFrontmatterName(markdown, newName);
            var library = await SaveWorkflowAsync(newName, InCurrentFormat(newName, markdown), origin, createOnly: true);
            return library.Where(w => string.Equals(w.Name, newName, StringComparison.OrdinalIgnoreCase)).ToArray();
        }

        /// <summary>A starter's fresh copy is written in the current format (F11). The shipped starters stay in format 1
        /// for the frozen v1 contract, and a byte copy of one opened under "This workflow uses an older format" at once.
        /// This runs the same upgrade a person reviews in the Studio (WorkflowUpgrade.Preview), one version at a time,
        /// and keeps a version only when its own check says the meaning is unchanged. Anything it cannot carry forward
        /// is written as it was, and the older-format notice then offers the reviewed upgrade as before.</summary>
        private static string InCurrentFormat(string name, string markdown)
        {
            var text = markdown;
            for (var hop = 0; hop < WorkflowParser.MaxSchemaVersion; hop++)
            {
                try
                {
                    var doc = DescribeWorkflowDocument(name, "user", name + ".md", WorkflowDocumentUtf8.GetBytes(text));
                    if (!doc.Metadata.Parses || doc.Metadata.SchemaVersion >= WorkflowParser.MaxSchemaVersion) return text;
                    var next = WorkflowUpgrade.Preview(doc, dryRun: true);
                    if (!next.CanApply || next.ProposedText == null) return text;
                    text = next.ProposedText;
                }
                catch (Exception ex) when (ex is InvalidOperationException || ex is FormatException) { return text; }
            }
            return text;
        }

        /// <summary>Rewrite the top-level <c>name:</c> line of a workflow's frontmatter, leaving every other byte as it
        /// was (line endings included), so a renamed copy of a starter differs from it only in its identity.</summary>
        private static string WithFrontmatterName(string markdown, string newName)
        {
            var lines = markdown.Split('\n');
            if (lines.Length == 0 || lines[0].TrimEnd('\r') != "---") return markdown;
            for (int i = 1; i < lines.Length && lines[i].TrimEnd('\r') != "---"; i++)
            {
                if (lines[i].Length == 0 || char.IsWhiteSpace(lines[i][0])) continue;
                if (FrontmatterKey(lines[i].Trim()).key != "name") continue;
                lines[i] = "name: " + newName + (lines[i].EndsWith("\r", StringComparison.Ordinal) ? "\r" : "");
                break;
            }
            return string.Join("\n", lines);
        }

        // ---- check_workflow for a TEMPLATE (§10.5) -----------------------------------------------

        /// <summary>Validate a template decls↔refs BOTH directions + that every slot carries an example, then run
        /// a TRIAL INSTANTIATION with the example values through the full admission (the structure invariant + the
        /// op-catalog/probe-binding dry-run). Offline-safe (a static resolve). A stock template must ship
        /// trial-clean. Nothing is saved.</summary>
        private async Task<WorkflowCheckReport> CheckTemplateAsync(WorkflowDef tmpl)
        {
            var report = new WorkflowCheckReport { Name = tmpl.Name };
            if (tmpl.Error != null) { report.ParseError = tmpl.Error; report.Ok = false; return report; }

            var findings = new List<CheckFinding>();
            var markdown = File.ReadAllText(tmpl.FilePath);
            var refs = SlotRefs(markdown);
            var declared = tmpl.Slots.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var r in refs.Where(r => !declared.Contains(r)).OrderBy(r => r, StringComparer.Ordinal))
                findings.Add(new CheckFinding { Severity = "warn", Message = $"{{{{{r}}}}} is referenced in the body but not declared in slots:." });
            foreach (var s in tmpl.Slots)
            {
                if (!refs.Contains(s.Name))
                    findings.Add(new CheckFinding { Severity = "warn", Message = $"slot '{s.Name}' is declared but never referenced with {{{{{s.Name}}}}} in the body." });
                if (string.IsNullOrWhiteSpace(s.Example))
                    findings.Add(new CheckFinding { Severity = "warn", Message = $"slot '{s.Name}' has no example: the trial instantiation (and the Studio fill-form) need one." });
                if (string.Equals(s.Required, "required", StringComparison.Ordinal) && !string.IsNullOrEmpty(s.Default))
                    findings.Add(new CheckFinding { Severity = "info", Message = $"slot '{s.Name}' is required yet declares a default: defaults apply only to optional slots, so it is ignored." });
            }

            if (tmpl.Slots.Length > 0 && tmpl.Slots.All(s => !string.IsNullOrWhiteSpace(s.Example)))
            {
                var examples = tmpl.Slots.ToDictionary(s => s.Name, s => s.Example, StringComparer.Ordinal);
                var (ok, diff) = CheckStructurePreserved(markdown, tmpl, examples);
                if (!ok)
                    findings.Add(new CheckFinding { Severity = "warn", Message = $"trial instantiation with the example values would change the enforced structure: {diff}" });
                else
                {
                    var trial = await CheckWorkflowDefAsync(WorkflowParser.Parse(RenderSlots(markdown, examples, tmpl.SchemaVersion)));
                    if (trial.ParseError != null)
                        findings.Add(new CheckFinding { Severity = "warn", Message = $"trial instantiation does not parse: {trial.ParseError}" });
                    foreach (var f in trial.Findings.Where(f => string.Equals(f.Severity, "warn", StringComparison.Ordinal)))
                        findings.Add(new CheckFinding { Severity = "warn", Message = "trial: " + f.Message });
                }
            }
            else if (tmpl.Slots.Length == 0)
                findings.Add(new CheckFinding { Severity = "info", Message = "template declares no slots: it renders to a fixed workflow (nothing to fill in)." });

            report.Findings = findings.ToArray();
            report.Ok = report.ParseError == null && !findings.Any(f => string.Equals(f.Severity, "warn", StringComparison.Ordinal));
            return report;
        }

        // ---- deterministic render + the structure-preserving invariant ---------------------------

        private static HashSet<string> SlotRefs(string text)
            => WorkflowParser.SlotRef.Matches(text ?? "").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        /// <summary>Pure text substitution of every <c>{{slotName}}</c> — no inference, ever (golden rule 1). A
        /// reference with no value is left verbatim (validation resolves all of them before this runs).</summary>
        private static string SubstituteSlots(string text, IReadOnlyDictionary<string, string> values)
            => WorkflowParser.SlotRef.Replace(text ?? "", m => values.TryGetValue(m.Groups[1].Value, out var v) ? (v ?? "") : m.Value);

        /// <summary>The substitution a template's format calls for. Format 1 is the plain text substitution it always
        /// had (frozen). From format 2 the file is strict YAML, so a slot answer that lands in YAML is written as YAML
        /// (lane l9-finish): inside a double-quoted scalar it is escaped, a plain scalar that holds one is written as a
        /// double-quoted scalar, and a block scalar's lines keep their indent. An answer like "FY26: July # final" then
        /// reads back exactly, and cannot end a scalar and start a key. Prose outside the YAML is substituted as it is.</summary>
        private static string RenderSlots(string text, IReadOnlyDictionary<string, string> values, int schemaVersion)
            => schemaVersion < 2 ? SubstituteSlots(text, values) : SubstituteSlotsYaml(text, values);

        private static readonly System.Text.Encodings.Web.JavaScriptEncoder YamlQuoteEncoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

        /// <summary>A YAML double-quoted scalar, JSON's escapes being a subset of YAML's.</summary>
        private static string YamlQuoted(string s) => JsonSerializer.Serialize(s ?? "", new JsonSerializerOptions { Encoder = YamlQuoteEncoder });

        private static readonly System.Text.RegularExpressions.Regex YamlScalarLine = new System.Text.RegularExpressions.Regex(
            @"^(?<lead>\s*(?:-\s+)?(?:[A-Za-z_][A-Za-z0-9_]*:\s+)?)(?<value>\S.*?)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string SubstituteSlotsYaml(string text, IReadOnlyDictionary<string, string> values)
        {
            string Value(System.Text.RegularExpressions.Match m) => values.TryGetValue(m.Groups[1].Value, out var v) ? (v ?? "") : null;
            var lines = (text ?? "").Split('\n');
            var inFront = lines.Length > 0 && lines[0].TrimEnd('\r') == "---";
            var inFence = false;
            var blockIndent = -1;   // the indent of the key that opened a block scalar (| or >), while in one
            for (var i = inFront ? 1 : 0; i < lines.Length; i++)
            {
                var raw = lines[i];
                var cr = raw.EndsWith("\r", StringComparison.Ordinal) ? "\r" : "";
                var line = cr.Length > 0 ? raw.Substring(0, raw.Length - 1) : raw;
                if (inFront && line == "---") { inFront = false; blockIndent = -1; continue; }
                if (!inFront)
                {
                    if (!inFence && line.StartsWith("```yaml", StringComparison.Ordinal)) { inFence = true; blockIndent = -1; continue; }
                    if (inFence && line.StartsWith("```", StringComparison.Ordinal)) { inFence = false; blockIndent = -1; continue; }
                }
                var yaml = inFront || inFence;
                if (!WorkflowParser.SlotRef.IsMatch(line))
                {
                    if (yaml) blockIndent = OpensBlock(line, blockIndent);
                    continue;
                }
                if (!yaml) { lines[i] = SubstituteSlots(line, values) + cr; continue; }

                var indent = line.Length - line.TrimStart().Length;
                if (blockIndent >= 0 && indent > blockIndent)
                {
                    // A line of a block scalar: an answer's line breaks keep the block's indent.
                    var pad = new string(' ', indent);
                    lines[i] = WorkflowParser.SlotRef.Replace(line, m => Value(m) is string v ? v.Replace("\r\n", "\n").Replace("\n", "\n" + pad) : m.Value) + cr;
                    continue;
                }
                blockIndent = -1;
                var parts = YamlScalarLine.Match(line);
                if (!parts.Success) { lines[i] = SubstituteSlots(line, values) + cr; continue; }
                var lead = parts.Groups["lead"].Value;
                var value = parts.Groups["value"].Value;
                if (value.StartsWith("\"", StringComparison.Ordinal))
                    // Inside a double-quoted scalar: the answer is escaped, so it cannot close the quote.
                    lines[i] = lead + WorkflowParser.SlotRef.Replace(value, m => Value(m) is string v ? YamlQuoted(v).Substring(1, YamlQuoted(v).Length - 2) : m.Value) + cr;
                else if (value.StartsWith("'", StringComparison.Ordinal))
                    lines[i] = lead + WorkflowParser.SlotRef.Replace(value, m => Value(m) is string v ? v.Replace("'", "''").Replace("\r\n", " ").Replace("\n", " ") : m.Value) + cr;
                else
                    // A plain scalar: the whole value, answer included, is written double-quoted.
                    lines[i] = lead + YamlQuoted(SubstituteSlots(value, values)) + cr;
            }
            return string.Join("\n", lines);

            static int OpensBlock(string line, int current)
            {
                var t = line.TrimEnd();
                if (t.EndsWith("|", StringComparison.Ordinal) || t.EndsWith(">", StringComparison.Ordinal)
                    || t.EndsWith("|-", StringComparison.Ordinal) || t.EndsWith(">-", StringComparison.Ordinal)
                    || t.EndsWith("|+", StringComparison.Ordinal) || t.EndsWith(">+", StringComparison.Ordinal))
                    return line.Length - line.TrimStart().Length;
                var indent = line.Length - line.TrimStart().Length;
                return current >= 0 && (t.Length == 0 || indent > current) ? current : -1;
            }
        }

        /// <summary>THE injection defence (§10.4 step 3). Render the template with the real values AND with a
        /// canonical dummy for every slot, parse both, and require their SKELETONS identical — step count/order,
        /// gate presence + strictness, each gate's inputs (name/type/required) and verifies (kind/probe/when/scope),
        /// its ops, and the frontmatter strictness/triggers. Question text, titles, descriptions, whenToUse and
        /// prose are EXCLUDED: slots may change what a step SAYS, never what the engine ENFORCES. On a mismatch,
        /// attributes the change to a single slot (by substituting one real value at a time) + names the delta.</summary>
        private static (bool ok, string diff) CheckStructurePreserved(string templateMarkdown, WorkflowDef tmpl, IReadOnlyDictionary<string, string> values)
        {
            var slotNames = tmpl.Slots.Select(s => s.Name).ToArray();
            var dummy = slotNames.ToDictionary(n => n, _ => "SLOTVALUE", StringComparer.Ordinal);
            var baseDef = WorkflowParser.Parse(RenderSlots(templateMarkdown, dummy, tmpl.SchemaVersion));
            var baseSkel = SkeletonOf(baseDef);

            if (SkeletonOf(WorkflowParser.Parse(RenderSlots(templateMarkdown, values, tmpl.SchemaVersion))) == baseSkel) return (true, null);

            foreach (var slot in slotNames)
            {
                var one = new Dictionary<string, string>(dummy, StringComparer.Ordinal);
                if (values.TryGetValue(slot, out var rv)) one[slot] = rv;
                var oneDef = WorkflowParser.Parse(RenderSlots(templateMarkdown, one, tmpl.SchemaVersion));
                if (SkeletonOf(oneDef) != baseSkel) return (false, DescribeStructuralDiff(slot, baseDef, oneDef));
            }
            return (false, "a slot value changes the workflow's enforced structure: slot values can change wording and values, never checks.");
        }

        /// <summary>The enforcement-relevant skeleton of a parsed def as a canonical string (prose excluded). Two
        /// renders whose skeletons are equal enforce identically. A parse fault is itself a skeleton difference.</summary>
        private static string SkeletonOf(WorkflowDef def)
        {
            if (def.Error != null) return "PARSE_ERROR: " + def.Error;
            var sb = new StringBuilder();
            sb.Append("strictness=").Append(def.Strictness ?? "").Append(";triggers=").Append(string.Join(",", def.Triggers ?? Array.Empty<string>())).Append('\n');
            foreach (var s in def.Steps)
            {
                var g = s.Gate;
                sb.Append("step ").Append(s.Number).Append(": gate=").Append(g != null ? "1" : "0");
                if (g != null)
                {
                    sb.Append(" gs=").Append(g.Strictness ?? "");
                    sb.Append(" in=").Append(string.Join("|", g.Inputs.Select(i => $"{i.Name}/{i.Type}/{i.Required}")));
                    sb.Append(" vf=").Append(string.Join("|", g.Verify.Select(v => $"{v.Kind}/{v.Probe}/{v.When}/{v.Scope}")));
                }
                sb.Append(" ops=").Append(string.Join(",", s.Ops ?? Array.Empty<string>()));
                // Format 3 (lane l9-finish): who does the step, its phase, what Semanticus runs and with what, and what it
                // asks and checks are what the engine ENFORCES too, so a slot may not change them either.
                sb.Append(" perf=").Append(s.Performer ?? "").Append(" ph=").Append(s.Phase ?? "");
                if (s.Action != null)
                    sb.Append(" act=").Append(s.Action.Op).Append('(')
                      .Append(string.Join(",", s.Action.Arguments.Select(a => a.Name + "=" + (a.Values != null ? string.Join("|", a.Values) : a.Value)))).Append(')');
                sb.Append(" q=").Append(string.Join("|", s.Questions.Select(q => $"{q.Name}/{q.Type}/{q.By}/{q.Required}")));
                sb.Append(" b=").Append(string.Join("|", s.Blanks.Select(b => $"{b.Name}/{b.Kind}/{b.Fills}/{b.By}")));
                sb.Append(" chk=").Append(string.Join("|", s.Checks.Select(c => $"{c.Kind}/{c.Phase}/{c.Target}/{c.Min}/{c.Max}/{string.Join(",", c.Of ?? Array.Empty<string>())}/{c.When}")));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private static string DescribeStructuralDiff(string slot, WorkflowDef baseDef, WorkflowDef realDef)
        {
            const string tail = ". Slot values can change wording and values, never checks.";
            if (realDef.Error != null && baseDef.Error == null)
                return $"the value for '{slot}' makes the rendered workflow no longer parse ({realDef.Error}){tail}";
            if (baseDef.Steps.Length != realDef.Steps.Length)
                return $"the value for '{slot}' changes the number of steps ({baseDef.Steps.Length} → {realDef.Steps.Length}){tail}";
            if ((baseDef.Strictness ?? "") != (realDef.Strictness ?? ""))
                return $"the value for '{slot}' changes the workflow strictness ('{baseDef.Strictness ?? "default"}' → '{realDef.Strictness ?? "default"}'){tail}";
            if (!(baseDef.Triggers ?? Array.Empty<string>()).SequenceEqual(realDef.Triggers ?? Array.Empty<string>()))
                return $"the value for '{slot}' changes the workflow triggers{tail}";
            for (int i = 0; i < baseDef.Steps.Length; i++)
            {
                var b = baseDef.Steps[i]; var r = realDef.Steps[i];
                bool bg = b.Gate != null, rg = r.Gate != null;
                if (bg != rg) return $"the value for '{slot}' {(rg ? "injects a gate into" : "removes the gate from")} Step {b.Number}{tail}";
                if (!(b.Ops ?? Array.Empty<string>()).SequenceEqual(r.Ops ?? Array.Empty<string>()))
                    return $"the value for '{slot}' changes Step {b.Number}'s declared ops{tail}";
                if (bg && rg)
                {
                    if ((b.Gate.Strictness ?? "") != (r.Gate.Strictness ?? ""))
                        return $"the value for '{slot}' changes Step {b.Number}'s gate strictness ('{b.Gate.Strictness ?? "inherit"}' → '{r.Gate.Strictness ?? "inherit"}'){tail}";
                    if (b.Gate.Inputs.Length != r.Gate.Inputs.Length)
                        return $"the value for '{slot}' changes the inputs on Step {b.Number}'s gate ({b.Gate.Inputs.Length} → {r.Gate.Inputs.Length}){tail}";
                    if (b.Gate.Verify.Length != r.Gate.Verify.Length)
                        return $"the value for '{slot}' injects an extra check (verify) into Step {b.Number}{tail}";
                    for (int k = 0; k < b.Gate.Inputs.Length; k++)
                    {
                        var bi = b.Gate.Inputs[k]; var ri = r.Gate.Inputs[k];
                        if (bi.Name != ri.Name || bi.Type != ri.Type || bi.Required != ri.Required)
                            return $"the value for '{slot}' changes an input on Step {b.Number}'s gate{tail}";
                    }
                    for (int k = 0; k < b.Gate.Verify.Length; k++)
                    {
                        var bv = b.Gate.Verify[k]; var rv = r.Gate.Verify[k];
                        if (bv.Kind != rv.Kind || bv.Probe != rv.Probe || bv.When != rv.When || bv.Scope != rv.Scope)
                            return $"the value for '{slot}' changes a check on Step {b.Number}'s gate{tail}";
                    }
                }
            }
            return $"the value for '{slot}' changes the workflow's enforced structure{tail}";
        }

        /// <summary>Render the final instance: substitute the values, then rewrite the frontmatter — drop
        /// <c>kind:</c> and the whole <c>slots:</c> block, set <c>name:</c> to newName, and append the §10.4
        /// provenance keys (template / template_version / instantiated / slot_values). Unknown keys survive the
        /// re-parse into Provenance, which check_workflow surfaces as an info finding — exactly right.</summary>
        private static string RenderInstance(string templateMarkdown, string newName, string templateName, int templateVersion,
            string slotValuesJson, IReadOnlyDictionary<string, string> values, int schemaVersion = 1)
        {
            var substituted = RenderSlots(templateMarkdown, values, schemaVersion);
            var lines = substituted.Replace("\r\n", "\n").Split('\n').ToList();
            int close = -1;
            for (int i = 1; i < lines.Count; i++) if (lines[i].TrimEnd() == "---") { close = i; break; }
            if (lines.Count == 0 || lines[0].TrimEnd() != "---" || close < 0) return substituted;   // defensive: templates always have frontmatter

            var fm = new List<string>();
            bool nameSet = false;
            for (int i = 1; i < close; i++)
            {
                var raw = lines[i];
                var indent = raw.Length - raw.TrimStart().Length;
                if (indent == 0)
                {
                    var (key, val) = FrontmatterKey(raw.Trim());
                    if (key == "kind") continue;                             // the instance is a workflow, not a template
                    if (key == "slots" && val == null)                       // drop the whole nested slots: block
                    {
                        int j = i + 1;
                        for (; j < close; j++)
                        {
                            if (string.IsNullOrWhiteSpace(lines[j])) continue;
                            if (lines[j].Length - lines[j].TrimStart().Length == 0) break;   // dedent → block ended
                        }
                        i = j - 1;
                        continue;
                    }
                    if (key == "name") { fm.Add("name: " + newName); nameSet = true; continue; }
                }
                fm.Add(raw);
            }
            if (!nameSet) fm.Add("name: " + newName);
            var version = templateVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var day = DateTime.UtcNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            if (schemaVersion >= 2)
            {
                // Format 2 and 3 have closed frontmatter keys, so the four provenance keys ride the format's own open
                // `provenance:` map (lane l9-finish). The parser reads each back into WorkflowDef.Provenance by the same
                // name, which is where month-end-close's certified totals are read from (ControlTotalsSlotText).
                var mine = new[]
                {
                    "  template: " + YamlQuoted(templateName),
                    "  template_version: " + version,
                    "  instantiated: " + YamlQuoted(day),
                    "  slot_values: " + YamlQuoted(slotValuesJson),
                };
                var at = fm.FindIndex(l => l.Length > 0 && !char.IsWhiteSpace(l[0]) && FrontmatterKey(l.Trim()).key == "provenance");
                if (at < 0) { fm.Add("provenance:"); fm.AddRange(mine); }
                else
                {
                    var end = at + 1;
                    while (end < fm.Count && (fm[end].Length == 0 || char.IsWhiteSpace(fm[end][0]))) end++;
                    fm.InsertRange(end, mine);
                }
            }
            else
            {
                fm.Add("template: " + templateName);
                fm.Add("template_version: " + version);
                fm.Add("instantiated: " + day);
                fm.Add("slot_values: " + slotValuesJson);
            }

            var outLines = new List<string> { "---" };
            outLines.AddRange(fm);
            outLines.Add("---");
            for (int i = close + 1; i < lines.Count; i++) outLines.Add(lines[i]);
            return string.Join("\n", outLines);
        }

        /// <summary>The key (and whether an inline value follows) of a trimmed frontmatter line — enough for the
        /// render surgery to spot kind/slots/name. A local mini-splitter (the parser's is private and richer).</summary>
        private static (string key, string val) FrontmatterKey(string trimmed)
        {
            var idx = trimmed.IndexOf(':');
            if (idx <= 0) return (null, null);
            var val = trimmed.Substring(idx + 1).Trim();
            return (trimmed.Substring(0, idx).Trim(), val.Length == 0 ? null : val);
        }

        private static Dictionary<string, string> ParseSlotValues(string valuesJson)
        {
            var vals = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(valuesJson)) return vals;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(valuesJson); }
            catch (Exception ex) { throw new InvalidOperationException("values must be a JSON object of {slotName: value}: " + ex.Message); }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("values must be a JSON OBJECT keyed by slot name, e.g. {\"surfaceName\": \"the FY26 Exec Dashboard\"}.");
                foreach (var p in doc.RootElement.EnumerateObject())
                    vals[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText();
            }
            return vals;
        }
    }
}
