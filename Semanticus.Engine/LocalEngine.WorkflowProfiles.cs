using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Semanticus.Engine
{
    public sealed partial class LocalEngine
    {
        private sealed class WorkflowProfileDef
        {
            public string Name;
            public string Title;
            public string Description;
            public string Strictness;
            public bool Pro;
            public (string op, string workflow, string mode)[] Bindings = Array.Empty<(string, string, string)>();
            public string[] Effects = Array.Empty<string>();
        }

        private static readonly WorkflowProfileDef[] WorkflowProfiles =
        {
            new WorkflowProfileDef
            {
                Name = "standard", Title = "Solo analyst",
                Description = "Keep every workflow available and let each playbook use its own checks. Nothing is required.",
                Effects = new[] { "Every workflow stays on the menu", "Nothing is required", "Each workflow keeps its own check strength" },
            },
            new WorkflowProfileDef
            {
                Name = "team-standard", Title = "Team standard", Strictness = "warn", Pro = true,
                Description = "Guide every new or edited measure through the reviewed measure playbook, without blocking the first rollout.",
                Bindings = new[]
                {
                    ("create_measure", "verified-measure", "warn"),
                    ("update_measure", "verified-measure", "warn"),
                },
                Effects = new[] { "New and edited measures use Verified measure", "Checks warn but let work continue", "Every workflow stays available" },
            },
            new WorkflowProfileDef
            {
                Name = "consulting-delivery", Title = "Consulting delivery", Strictness = "hard", Pro = true,
                Description = "Require evidence before measures or relationships are handed over to a client.",
                Bindings = new[]
                {
                    ("create_measure", "verified-measure", "hard"),
                    ("update_measure", "verified-measure", "hard"),
                    ("create_relationship", "add-relationship", "hard"),
                },
                Effects = new[] { "New and edited measures use Verified measure", "New relationships use Add relationship", "Checks block until they pass or are explicitly overridden" },
            },
        };

        public Task<WorkflowProfileInfo[]> ListWorkflowProfilesAsync()
        {
            var active = ActiveWorkflowProfile();
            return Task.FromResult(WorkflowProfiles.Select(p => new WorkflowProfileInfo
            {
                Name = p.Name, Title = p.Title, Description = p.Description, Effects = p.Effects,
                Pro = p.Pro, Selected = string.Equals(active, p.Name, StringComparison.Ordinal),
            }).ToArray());
        }

        public async Task<WorkflowProfileResult> ActivateWorkflowProfileAsync(string name, string origin)
        {
            var profile = WorkflowProfiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Workflow profile '" + name + "' was not found. list_workflow_profiles shows the available profiles.");
            var file = WorkflowSettingsFile()
                ?? throw new InvalidOperationException("No workspace can hold workflow settings. Open a model or workspace, then retry.");
            if (profile.Pro)
                Entitlement.EntitlementGuard.RequirePro(_entitlement, "activate_workflow_profile (applying a profile with required workflows)",
                    "Free alternative: use the Solo analyst profile, or follow any workflow manually from list_workflows.");

            var library = LoadWorkflowDefs().Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
            var missing = profile.Bindings.Select(x => x.workflow).Where(x => !library.Contains(x)).Distinct(StringComparer.Ordinal).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException("This profile needs missing workflow(s): " + string.Join(", ", missing) + ". Restore the stock workflow library, then retry.");

            MutateWorkflowSettings(file, root =>
            {
                // A profile owns the whole simple policy layer. Unknown future keys survive, while prior menu,
                // requirement and activation choices are replaced atomically so Standard truly resets the project.
                root.Remove("workflows");
                root.Remove("bindings");
                root.Remove("activation");
                root.Remove("strictness");
                if (profile.Strictness != null) root["strictness"] = profile.Strictness;
                if (profile.Bindings.Length > 0)
                {
                    var bindings = new JsonObject();
                    foreach (var binding in profile.Bindings)
                        bindings[binding.op] = new JsonObject
                        {
                            ["require"] = new JsonArray((JsonNode)binding.workflow),
                            ["mode"] = binding.mode,
                        };
                    root["bindings"] = bindings;
                }
                root["profile"] = new JsonObject
                {
                    ["name"] = profile.Name,
                    ["selectedUtc"] = DateTime.UtcNow.ToString("o"),
                    ["selectedBy"] = string.IsNullOrWhiteSpace(origin) ? "human" : origin,
                };
            });

            var workflows = await PublishWorkflowLibraryAsync();
            _sessions.Bus.PublishActivity(new ActivityEvent
            {
                Origin = string.IsNullOrWhiteSpace(origin) ? "human" : origin,
                Kind = "activate_workflow_profile", Target = profile.Name, Ok = true,
                Label = "Workflow profile changed to " + profile.Title,
            });
            return new WorkflowProfileResult
            {
                ActiveProfile = profile.Name,
                Workflows = workflows,
                Policy = await GetWorkflowPolicyAsync(),
                Note = profile.Title + " is now the project workflow profile. A later policy change through Studio or the AI Assistant will mark it Custom.",
            };
        }

        private string ActiveWorkflowProfile()
        {
            var file = WorkflowSettingsFile();
            if (file == null || !File.Exists(file)) return "standard";
            try
            {
                using var doc = JsonDocument.Parse(ReadSettingsStrict(file));
                var root = doc.RootElement;
                if (root.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object
                    && profile.TryGetProperty("name", out var name) && !string.IsNullOrWhiteSpace(name.GetString()))
                    return name.GetString();
                return root.EnumerateObject().Any() ? "custom" : "standard";
            }
            catch { return "custom"; }
        }
    }
}
