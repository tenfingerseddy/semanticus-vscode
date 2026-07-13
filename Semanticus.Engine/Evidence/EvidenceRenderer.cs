using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Semanticus.Engine.Evidence
{
    /// <summary>
    /// The ONE renderer for the ONE artifact format. Turns an <see cref="EvidenceDoc"/> into a self-contained HTML
    /// page: inline CSS, no external resources, the Ink + restrained Signal-green brand, native fonts, light and
    /// dark via prefers-color-scheme, and print-friendly (a certificate gets saved to PDF). It is DETERMINISTIC -
    /// no clocks, no randomness, everything comes from the document - so the same document always renders the same
    /// bytes (pinned by a golden-file test).
    ///
    /// Two structural rules live in this control flow: a verdict badge is emitted ONLY when the document carries
    /// coverage (a grade with nothing measured behind it never shows), and unknowns are named in the footer, never
    /// quietly turned green. Customer-facing copy: no em-dashes, plain words for the tamper-evidence line, and the
    /// origin "agent" reads as "AI Assistant".
    /// </summary>
    public static class EvidenceRenderer
    {
        private const string Dot = " · ";   // middle dot separator (not an em-dash)

        public static string Render(EvidenceDoc doc)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            // Validate BEFORE drawing a single byte. The structural rules (a chip owes coverage, an overridden badge
            // owes a reason, counts cannot contradict coverage) are enforced here so the renderer can never emit a
            // certificate that the validator would have rejected. Render and Write share the exact same gate.
            doc.Validate();
            var sb = new StringBuilder(4096);

            sb.Append("<!doctype html>\n");
            sb.Append("<html lang=\"en\">\n");
            sb.Append("<head>\n");
            sb.Append("<meta charset=\"utf-8\">\n");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            sb.Append("<title>").Append(H(doc.Title)).Append(Dot).Append("evidence</title>\n");
            sb.Append("<style>\n").Append(Css).Append("\n</style>\n");
            sb.Append("</head>\n");
            sb.Append("<body>\n");
            sb.Append("<main class=\"ev\">\n");
            sb.Append("<div class=\"ev-card\">\n");

            RenderHeader(doc, sb);

            if (doc.Sections != null)
                foreach (var s in doc.Sections)
                    RenderSection(s, sb);

            RenderFooter(doc, sb);

            sb.Append("</div>\n");
            sb.Append("</main>\n");
            sb.Append("</body>\n");
            sb.Append("</html>\n");
            return sb.ToString();
        }

        // ---- header ----

        private static void RenderHeader(EvidenceDoc doc, StringBuilder sb)
        {
            sb.Append("<header class=\"ev-head\">\n");
            if (!string.IsNullOrWhiteSpace(doc.Kind))
                sb.Append("<div class=\"ev-kind\">").Append(H(doc.Kind)).Append("</div>\n");
            sb.Append("<h1 class=\"ev-title\">").Append(H(doc.Title)).Append("</h1>\n");

            // STRUCTURAL RULE: no coverage, no verdict badge. A grade must show what it measured. Validate has
            // already guaranteed that a non-null Coverage carries a non-null Verdict, so .Value is safe here.
            if (doc.Coverage != null)
            {
                sb.Append("<div class=\"ev-verdict\">");
                sb.Append(Chip(doc.Verdict.Value, large: true));
                sb.Append("<span class=\"ev-coverage\">").Append(H(CoverageLine(doc))).Append("</span>");
                sb.Append("</div>\n");
            }

            if (doc.Verdict == Verdict.Overridden && !string.IsNullOrWhiteSpace(doc.OverrideReason))
            {
                sb.Append("<div class=\"ev-override\"><span class=\"ev-override-label\">Override reason</span> ")
                  .Append(H(doc.OverrideReason)).Append("</div>\n");
            }

            RenderIdentity(doc, sb);

            var produced = ProducedBy(doc);
            if (produced.Length > 0)
                sb.Append("<div class=\"ev-produced\">").Append(H(produced)).Append("</div>\n");

            sb.Append("</header>\n");
        }

        private static void RenderIdentity(EvidenceDoc doc, StringBuilder sb)
        {
            var rows = new List<KeyValuePair<string, string>>();
            AddIf(rows, "Model", doc.ModelName);
            AddIf(rows, "Fingerprint", doc.ModelFingerprint);
            AddIf(rows, "Base commit", ShortHash(doc.BaseCommit));
            AddIf(rows, "Session", doc.SessionId);
            if (doc.Revision.HasValue) rows.Add(new KeyValuePair<string, string>("Revision", doc.Revision.Value.ToString(CultureInfo.InvariantCulture)));
            if (rows.Count == 0) return;

            sb.Append("<dl class=\"ev-id\">\n");
            foreach (var r in rows)
                sb.Append("<div><dt>").Append(H(r.Key)).Append("</dt><dd>").Append(H(r.Value)).Append("</dd></div>\n");
            sb.Append("</dl>\n");
        }

        private static string CoverageLine(EvidenceDoc doc)
        {
            var c = doc.Coverage;
            var parts = new List<string> { "Verified " + N(c.Verified) + " of " + N(c.Total) };
            if (c.Unknowns > 0) parts.Add(N(c.Unknowns) + " unknown");
            var nr = CountOf(doc, "NeedsReview"); if (nr > 0) parts.Add(N(nr) + " need review");
            var br = CountOf(doc, "Broken"); if (br > 0) parts.Add(N(br) + " broken");
            var ov = CountOf(doc, "Overridden"); if (ov > 0) parts.Add(N(ov) + " overridden");
            return string.Join(Dot, parts);
        }

        private static string ProducedBy(EvidenceDoc doc)
        {
            var bits = new List<string>();
            if (!string.IsNullOrWhiteSpace(doc.Producer))
            {
                var p = "Produced by " + doc.Producer.Trim();
                if (!string.IsNullOrWhiteSpace(doc.ProducerVersion)) p += " " + doc.ProducerVersion.Trim();
                bits.Add(p);
            }
            var origin = OriginLabel(doc.Origin);
            if (origin != null) bits.Add(origin);
            if (!string.IsNullOrWhiteSpace(doc.CreatedUtc)) bits.Add(doc.CreatedUtc.Trim());
            return string.Join(Dot, bits);
        }

        // "agent" reads as "AI Assistant" per the tool-wide UI convention (never "Claude"); the others title-case.
        private static string OriginLabel(string origin)
        {
            if (string.IsNullOrWhiteSpace(origin)) return null;
            switch (origin.Trim().ToLowerInvariant())
            {
                case "agent": return "AI Assistant";
                case "human": return "Human";
                case "system": return "System";
                default: return origin.Trim();
            }
        }

        // ---- sections ----

        private static void RenderSection(EvidenceSection section, StringBuilder sb)
        {
            switch (section)
            {
                case SummarySection s: RenderSummary(s, sb); break;
                case KeyValueSection s: RenderKeyValue(s, sb); break;
                case FindingsSection s: RenderFindings(s, sb); break;
                case DiffSection s: RenderDiff(s, sb); break;
                case ProbeSection s: RenderProbe(s, sb); break;
                case StepsSection s: RenderSteps(s, sb); break;
                case NoteSection s: RenderNote(s, sb); break;
                default: /* unknown section kinds are skipped, never guessed */ break;
            }
        }

        private static void RenderSummary(SummarySection s, StringBuilder sb)
        {
            OpenSection("summary", s.Title, sb);
            if (s.Paragraphs != null)
                foreach (var p in s.Paragraphs)
                    sb.Append("<p>").Append(H(p)).Append("</p>\n");
            CloseSection(sb);
        }

        private static void RenderKeyValue(KeyValueSection s, StringBuilder sb)
        {
            OpenSection("kv", s.Title, sb);
            sb.Append("<table class=\"ev-table ev-kv-table\"><tbody>\n");
            if (s.Pairs != null)
                foreach (var kv in s.Pairs)
                    sb.Append("<tr><th scope=\"row\">").Append(H(kv.Key)).Append("</th><td>").Append(H(kv.Value)).Append("</td></tr>\n");
            sb.Append("</tbody></table>\n");
            CloseSection(sb);
        }

        private static void RenderFindings(FindingsSection s, StringBuilder sb)
        {
            OpenSectionWithRollup("findings", s.Title, s.Rollup(), sb);
            sb.Append("<table class=\"ev-table ev-findings-table\">\n");
            sb.Append("<thead><tr><th>Finding</th><th>Status</th><th>Detail</th></tr></thead>\n<tbody>\n");
            if (s.Rows != null)
                foreach (var r in s.Rows)
                {
                    var name = H(r.Name);
                    if (r.Count.HasValue) name += " <span class=\"ev-count\">(" + N(r.Count.Value) + ")</span>";
                    sb.Append("<tr><td>").Append(name).Append("</td><td>").Append(Chip(r.Verdict)).Append("</td><td>")
                      .Append(H(r.Detail)).Append("</td></tr>\n");
                }
            sb.Append("</tbody></table>\n");
            CloseSection(sb);
        }

        private static void RenderDiff(DiffSection s, StringBuilder sb)
        {
            OpenSection("diff", s.Title, sb);
            if (!string.IsNullOrWhiteSpace(s.Language))
                sb.Append("<div class=\"ev-diff-lang\">").Append(H(s.Language)).Append("</div>\n");
            sb.Append("<div class=\"ev-diff\">\n");
            sb.Append("<div class=\"ev-diff-side ev-diff-before\"><div class=\"ev-diff-label\">Before</div><pre><code>")
              .Append(H(s.Before)).Append("</code></pre></div>\n");
            sb.Append("<div class=\"ev-diff-side ev-diff-after\"><div class=\"ev-diff-label\">After</div><pre><code>")
              .Append(H(s.After)).Append("</code></pre></div>\n");
            sb.Append("</div>\n");
            CloseSection(sb);
        }

        private static void RenderProbe(ProbeSection s, StringBuilder sb)
        {
            OpenSection("probe", s.Title, sb);
            sb.Append("<table class=\"ev-table ev-probe-table\">\n");
            sb.Append("<thead><tr><th>Query</th><th>Expected</th><th>Actual</th><th>Status</th><th>Time</th></tr></thead>\n<tbody>\n");
            if (s.Probes != null)
                foreach (var p in s.Probes)
                {
                    sb.Append("<tr><td><code>").Append(H(p.Query)).Append("</code></td><td>").Append(H(p.Expected))
                      .Append("</td><td>").Append(H(p.Actual)).Append("</td><td>").Append(Chip(p.Verdict)).Append("</td><td>")
                      .Append(p.DurationMs.HasValue ? H(Ms(p.DurationMs.Value)) : "").Append("</td></tr>\n");
                }
            sb.Append("</tbody></table>\n");
            CloseSection(sb);
        }

        private static void RenderSteps(StepsSection s, StringBuilder sb)
        {
            OpenSection("steps", s.Title, sb);
            sb.Append("<ol class=\"ev-steps\">\n");
            if (s.Steps != null)
                foreach (var st in s.Steps)
                {
                    sb.Append("<li><div class=\"ev-step-head\">").Append(Chip(st.Verdict))
                      .Append("<span class=\"ev-step-name\">").Append(H(st.Name)).Append("</span>");
                    if (!string.IsNullOrWhiteSpace(st.WhenUtc))
                        sb.Append("<span class=\"ev-step-when\">").Append(H(st.WhenUtc)).Append("</span>");
                    sb.Append("</div>");
                    if (!string.IsNullOrWhiteSpace(st.Note))
                        sb.Append("<div class=\"ev-step-note\">").Append(H(st.Note)).Append("</div>");
                    sb.Append("</li>\n");
                }
            sb.Append("</ol>\n");
            CloseSection(sb);
        }

        private static void RenderNote(NoteSection s, StringBuilder sb)
        {
            // Tone reaches class-attribute context, so it is never interpolated from the document: Validate has
            // already pinned s.Tone to the closed vocabulary, and the emitted token is one of OUR two literals.
            var tone = string.Equals(s.Tone, "warning", StringComparison.Ordinal) ? "warning" : "info";
            sb.Append("<section class=\"ev-sec ev-note ev-note-").Append(tone).Append("\">\n");
            if (!string.IsNullOrWhiteSpace(s.Title))
                sb.Append("<h2 class=\"ev-sec-title\">").Append(H(s.Title)).Append("</h2>\n");
            if (!string.IsNullOrWhiteSpace(s.Text))
                sb.Append("<p>").Append(H(s.Text)).Append("</p>\n");
            sb.Append("</section>\n");
        }

        private static void OpenSection(string kind, string title, StringBuilder sb)
        {
            sb.Append("<section class=\"ev-sec ev-").Append(kind).Append("\">\n");
            if (!string.IsNullOrWhiteSpace(title))
                sb.Append("<h2 class=\"ev-sec-title\">").Append(H(title)).Append("</h2>\n");
        }

        private static void OpenSectionWithRollup(string kind, string title, Verdict rollup, StringBuilder sb)
        {
            sb.Append("<section class=\"ev-sec ev-").Append(kind).Append("\">\n");
            sb.Append("<h2 class=\"ev-sec-title\">");
            if (!string.IsNullOrWhiteSpace(title)) sb.Append(H(title)).Append(' ');
            sb.Append(Chip(rollup)).Append("</h2>\n");
        }

        private static void CloseSection(StringBuilder sb) => sb.Append("</section>\n");

        // ---- footer ----

        private static void RenderFooter(EvidenceDoc doc, StringBuilder sb)
        {
            sb.Append("<footer class=\"ev-foot\">\n");

            // Honesty line: unknowns are named, never converted to green.
            if (doc.Coverage != null && doc.Coverage.Unknowns > 0)
                sb.Append("<p class=\"ev-honesty\">This report names ").Append(N(doc.Coverage.Unknowns))
                  .Append(" item(s) it could not check. Unknown is not counted as verified.</p>\n");

            if (!string.IsNullOrWhiteSpace(doc.ContentHash))
            {
                sb.Append("<p class=\"ev-hash\">Tamper-evident record. Content signature <code>")
                  .Append(H(ShortHash(doc.ContentHash))).Append("</code></p>\n");
                sb.Append("<p class=\"ev-hash-full\"><code>").Append(H(doc.ContentHash)).Append("</code></p>\n");
                if (!string.IsNullOrWhiteSpace(doc.PrevHash))
                    sb.Append("<p class=\"ev-hash\">Linked to the prior record <code>")
                      .Append(H(ShortHash(doc.PrevHash))).Append("</code></p>\n");
            }

            sb.Append("<p class=\"ev-mark\">Produced by Semanticus</p>\n");
            sb.Append("</footer>\n");
        }

        // ---- small helpers ----

        private static string Chip(Verdict v, bool large = false)
            => "<span class=\"v-chip v-" + Verdicts.Slug(v) + (large ? " v-lg" : "") + "\">" + H(Verdicts.Label(v)) + "</span>";

        private static void AddIf(List<KeyValuePair<string, string>> rows, string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)) rows.Add(new KeyValuePair<string, string>(key, value));
        }

        private static int CountOf(EvidenceDoc doc, string word)
            => doc.VerdictCounts != null && doc.VerdictCounts.TryGetValue(word, out var n) ? n : 0;

        private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);

        private static string Ms(double ms) => ms.ToString("0.###", CultureInfo.InvariantCulture) + " ms";

        private static string ShortHash(string hash)
            => string.IsNullOrEmpty(hash) || hash.Length <= 12 ? hash : hash.Substring(0, 12);

        private static string H(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (var c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    case '\'': sb.Append("&#39;"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        // The stylesheet, assembled from single-line pieces joined with "\n" so the emitted bytes never depend on the
        // source file's line endings (the golden-file pin would otherwise drift between platforms). Ink + a restrained
        // Signal green; native font stack; both themes; print-friendly. No em-dashes anywhere in here.
        private static readonly string Css = string.Join("\n", new[]
        {
            ":root{--bg:#f8fafc;--card:#ffffff;--ink:#0f172a;--muted:#475569;--line:#e2e8f0;--soft:#f1f5f9;--accent:#16a34a}",
            "*{box-sizing:border-box}",
            "html,body{margin:0;padding:0}",
            "body{background:var(--bg);color:var(--ink);font-family:-apple-system,BlinkMacSystemFont,\"Segoe UI\",Roboto,Helvetica,Arial,sans-serif;font-size:15px;line-height:1.5}",
            "code,pre{font-family:ui-monospace,SFMono-Regular,\"SF Mono\",Menlo,Consolas,\"Liberation Mono\",monospace}",
            ".ev{max-width:820px;margin:0 auto;padding:32px 20px}",
            ".ev-card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:28px}",
            ".ev-head{border-bottom:1px solid var(--line);padding-bottom:18px;margin-bottom:6px}",
            ".ev-kind{display:inline-block;font-size:11px;letter-spacing:.08em;text-transform:uppercase;color:var(--muted);background:var(--soft);border:1px solid var(--line);border-radius:999px;padding:3px 10px}",
            ".ev-title{font-size:24px;line-height:1.25;margin:12px 0 10px}",
            ".ev-verdict{display:flex;align-items:center;gap:12px;flex-wrap:wrap;margin:6px 0 14px}",
            ".ev-coverage{color:var(--muted);font-size:14px}",
            ".ev-override{background:#f5f3ff;border:1px solid #ddd6fe;color:#5b21b6;border-radius:8px;padding:8px 12px;margin:0 0 14px;font-size:14px}",
            ".ev-override-label{font-weight:700;margin-right:6px}",
            ".ev-id{display:grid;grid-template-columns:1fr 1fr;gap:4px 24px;margin:8px 0 0;font-size:13px}",
            ".ev-id div{display:flex;gap:8px}",
            ".ev-id dt{color:var(--muted);min-width:92px}",
            ".ev-id dd{margin:0;color:var(--ink);word-break:break-word}",
            ".ev-produced{color:var(--muted);font-size:13px;margin-top:12px}",
            ".ev-sec{margin:24px 0}",
            ".ev-sec-title{font-size:16px;margin:0 0 10px;display:flex;align-items:center;gap:8px}",
            ".ev-sec p{margin:0 0 10px}",
            ".ev-table{width:100%;border-collapse:collapse;font-size:14px}",
            ".ev-table th,.ev-table td{text-align:left;vertical-align:top;padding:8px 10px;border-bottom:1px solid var(--line)}",
            ".ev-table thead th{color:var(--muted);font-weight:600;font-size:12px;text-transform:uppercase;letter-spacing:.04em}",
            ".ev-kv-table th{color:var(--muted);font-weight:600;width:38%}",
            ".ev-count{color:var(--muted);font-weight:400}",
            ".ev-diff-lang{display:inline-block;font-size:11px;text-transform:uppercase;letter-spacing:.06em;color:var(--muted);margin-bottom:8px}",
            ".ev-diff{display:grid;grid-template-columns:1fr 1fr;gap:12px}",
            ".ev-diff-label{font-size:12px;color:var(--muted);margin-bottom:4px}",
            ".ev-diff pre{margin:0;background:var(--soft);border:1px solid var(--line);border-radius:8px;padding:10px;overflow-x:auto;font-size:13px;white-space:pre-wrap;word-break:break-word}",
            ".ev-steps{margin:0;padding-left:0;list-style:none}",
            ".ev-steps li{padding:8px 0;border-bottom:1px solid var(--line)}",
            ".ev-step-head{display:flex;align-items:center;gap:10px}",
            ".ev-step-name{font-weight:600}",
            ".ev-step-when{color:var(--muted);font-size:12px;margin-left:auto}",
            ".ev-step-note{color:var(--muted);font-size:13px;margin-top:4px;padding-left:2px}",
            ".ev-note{border-radius:8px;padding:12px 14px;border:1px solid var(--line)}",
            ".ev-note-info{background:var(--soft)}",
            ".ev-note-warning{background:#fffbeb;border-color:#fde68a;color:#92400e}",
            ".v-chip{display:inline-block;padding:2px 9px;border-radius:999px;font-size:12px;font-weight:600;line-height:1.6;border:1px solid transparent;white-space:nowrap}",
            ".v-lg{font-size:14px;padding:5px 14px}",
            ".v-verified{background:#dcfce7;color:#166534;border-color:#bbf7d0}",
            ".v-needsreview{background:#fef3c7;color:#92400e;border-color:#fde68a}",
            ".v-broken{background:#fee2e2;color:#991b1b;border-color:#fecaca}",
            ".v-unknown{background:#f1f5f9;color:#475569;border-color:#e2e8f0}",
            ".v-overridden{background:#ede9fe;color:#5b21b6;border-color:#ddd6fe}",
            ".ev-foot{border-top:1px solid var(--line);margin-top:24px;padding-top:16px;font-size:13px;color:var(--muted)}",
            ".ev-honesty{color:var(--ink);font-weight:600}",
            ".ev-hash{margin:4px 0}",
            ".ev-hash-full{margin:2px 0 10px;word-break:break-all;font-size:11px}",
            ".ev-mark{margin:8px 0 0;font-weight:600;color:var(--accent)}",
            "@media (prefers-color-scheme:dark){",
            ":root{--bg:#0b1220;--card:#0f172a;--ink:#e5e7eb;--muted:#94a3b8;--line:#1f2937;--soft:#111c30;--accent:#22c55e}",
            ".ev-override{background:#241a3a;border-color:#4c1d95;color:#c4b5fd}",
            ".ev-note-warning{background:#3a2a0a;border-color:#713f12;color:#fcd34d}",
            ".v-verified{background:#052e16;color:#86efac;border-color:#14532d}",
            ".v-needsreview{background:#3a2a0a;color:#fcd34d;border-color:#713f12}",
            ".v-broken{background:#3f1416;color:#fca5a5;border-color:#7f1d1d}",
            ".v-unknown{background:#1e293b;color:#cbd5e1;border-color:#334155}",
            ".v-overridden{background:#241a3a;color:#c4b5fd;border-color:#4c1d95}",
            "}",
            "@media print{body{background:#ffffff}.ev{padding:0}.ev-card{border:none;border-radius:0;padding:0}.ev-sec,.ev-steps li,.ev-diff-side{page-break-inside:avoid}}",
            "@media (max-width:640px){.ev-id{grid-template-columns:1fr}.ev-diff{grid-template-columns:1fr}}",
        });
    }
}
