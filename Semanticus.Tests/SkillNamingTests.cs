using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Semanticus.Tests
{
    /// <summary>Every repo-specific skill must carry its repo suffix.
    ///
    /// Why: Rome and Semanticus are normally open in the same session, so one repo's
    /// <c>.claude/skills/</c> is always visible to the other. Two skills sharing a directory name
    /// collide, and the loser is simply absent. On 2026-07-29 an agent working in Semanticus loaded
    /// ROME's verify battery, which omitted Semanticus.Tests and the coverage oracle, and the oracle
    /// was failing. The wrong battery reported green.
    ///
    /// HONEST LIMITATION, so nobody trusts this further than it goes: this test runs against the tree
    /// it is checked out in, so it CANNOT fire inside a stale worktree or an old branch, which is
    /// where the 2026-07-29 failure actually came from. It PREVENTS REINTRODUCTION on main. It does
    /// not detect the state of any other checkout, and it does not replace the Step 0 repo assertion
    /// inside each skill, which travels with the file and therefore works on every branch.</summary>
    public sealed class SkillNamingTests
    {
        private const string Suffix = "semanticus";

        /// <summary>Names that exist in BOTH repos and therefore must never appear bare.</summary>
        private static readonly string[] CollidingFamily = { "verify", "plan-review", "progress-artifact" };

        /// <summary>Walks up from the test assembly to the directory holding Semanticus.sln.
        /// Skipped rather than failed when the marker is absent, so a clean-package or
        /// source-less run does not report a false violation.</summary>
        private static string? FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Semanticus.sln")))
                dir = dir.Parent;
            return dir?.FullName;
        }

        [Fact]
        public void No_skill_directory_carries_a_bare_colliding_name()
        {
            var root = FindRepoRoot();
            if (root == null) return; // no source tree beside the assembly; nothing to assert

            var skills = Path.Combine(root, ".claude", "skills");
            Assert.True(Directory.Exists(skills), $"Expected the skill home at {skills}");

            var bare = Directory.GetDirectories(skills)
                .Select(Path.GetFileName)
                .Where(n => n != null && CollidingFamily.Contains(n!, StringComparer.Ordinal))
                .ToArray();

            Assert.True(bare.Length == 0,
                "These skill directories carry a bare colliding name and must be suffixed with '-" + Suffix +
                "': " + string.Join(", ", bare) +
                ". Rome defines a skill of the same name, so a bare name means one of the two is silently " +
                "unavailable to every agent in a session that has both repos open.");
        }

        [Fact]
        public void Every_skill_frontmatter_name_matches_its_directory()
        {
            var root = FindRepoRoot();
            if (root == null) return;

            var skills = Path.Combine(root, ".claude", "skills");
            var failures = new List<string>();

            foreach (var dir in Directory.GetDirectories(skills))
            {
                var name = Path.GetFileName(dir);
                var skillMd = Path.Combine(dir, "SKILL.md");
                if (!File.Exists(skillMd)) { failures.Add($"{name}/ has no SKILL.md"); continue; }

                var text = File.ReadAllText(skillMd);
                var block = Regex.Match(text, @"\A---\r?\n(.*?)\r?\n---", RegexOptions.Singleline);
                if (!block.Success) { failures.Add($"{name}/SKILL.md has no frontmatter block"); continue; }

                var declared = Regex.Match(block.Groups[1].Value, @"^name:\s*(.+?)\s*$", RegexOptions.Multiline);
                if (!declared.Success) { failures.Add($"{name}/SKILL.md frontmatter has no name: field"); continue; }

                if (!string.Equals(declared.Groups[1].Value, name, StringComparison.Ordinal))
                    failures.Add($"{name}/SKILL.md declares name: '{declared.Groups[1].Value}'");
            }

            Assert.True(failures.Count == 0,
                "The frontmatter name is the advertised identity and can drift away from the directory on its " +
                "own, so these must match: " + string.Join("; ", failures));
        }
    }
}
