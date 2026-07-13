using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Semanticus.Engine;
using Semanticus.Engine.Entitlement;
using Xunit;
using EngineActivityEvent = Semanticus.Engine.ActivityEvent;

namespace Semanticus.Tests
{
    /// <summary>T125 anchors the supported branch, checkout, pull and clone operations through their public MCP
    /// wrappers. The fixtures own every repository and remote, so no assertion depends on the ambient checkout,
    /// configured identity, network, current directory or global git state.</summary>
    public sealed class GitPublicOperationTests
    {
        private sealed class Free : IEntitlement
        {
            public bool IsPro => false;
            public EntitlementInfo Info => new EntitlementInfo { Tier = "free" };
        }

        [Fact]
        public async Task Branch_and_checkout_report_only_real_model_reload_and_broadcast_activity_not_model_changes()
        {
            var repo = RepoWithModelChangeBranch("git-public-branch-");
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Free(), repo.Dir);
                await engine.OpenAsync(repo.Model);
                var activities = new List<EngineActivityEvent>();
                var modelChanges = 0;
                sessions.Bus.Activity += activities.Add;
                sessions.Bus.Changed += _ => modelChanges++;

                var listed = await McpTools.GitBranch(engine);
                Assert.True(listed.Ok, listed.Error);
                Assert.Contains("main", listed.Output);
                Assert.Contains("model-change", listed.Output);
                Assert.Empty(activities);

                var created = await McpTools.GitBranch(engine, "topic", create: true);
                Assert.True(created.Ok, created.Error);
                Assert.Equal("main", created.Branch);
                Assert.False(created.ModelReloadNeeded);
                Assert.Single(activities);
                Assert.Equal("git_branch", activities[0].Kind);
                Assert.True(activities[0].Ok);

                GitActionResult switchedSameTree;
                using (File.Open(repo.Model, FileMode.Open, FileAccess.Read, FileShare.None))
                    switchedSameTree = await McpTools.GitBranch(engine, "topic", checkout: true);
                Assert.True(switchedSameTree.Ok, switchedSameTree.Error);
                Assert.Equal("topic", switchedSameTree.Branch);
                Assert.False(switchedSameTree.ModelReloadNeeded);
                Assert.Null(switchedSameTree.ModelPath);

                var switchedModel = await McpTools.GitCheckout(engine, "model-change");
                Assert.True(switchedModel.Ok, switchedModel.Error);
                Assert.Equal("model-change", switchedModel.Branch);
                Assert.True(switchedModel.ModelReloadNeeded);
                Assert.Equal(Path.GetFullPath(repo.Model), switchedModel.ModelPath);
                Assert.Equal(3, activities.Count);
                Assert.All(activities, activity => Assert.Equal("agent", activity.Origin));
                Assert.Equal(0, modelChanges);
            }
            finally { Delete(repo.Root); }
        }

        [Fact]
        public async Task Branch_and_checkout_fail_closed_without_a_repo_or_with_unsaved_model_edits()
        {
            var loose = Path.Combine(Path.GetTempPath(), "git-public-loose-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(loose);
            var looseModel = Path.Combine(loose, "model.bim");
            File.Copy(TestModels.FindBim(), looseModel);
            try
            {
                var looseSessions = new SessionManager();
                using var looseEngine = new LocalEngine(looseSessions, new Free(), loose);
                await looseEngine.OpenAsync(looseModel);
                var looseActivities = new List<EngineActivityEvent>();
                looseSessions.Bus.Activity += looseActivities.Add;

                var listed = await McpTools.GitBranch(looseEngine);
                Assert.False(listed.Ok);
                Assert.Contains("not inside a git repository", listed.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Empty(looseActivities);

                var checkout = await McpTools.GitCheckout(looseEngine, "main");
                Assert.False(checkout.Ok);
                Assert.False(checkout.ModelReloadNeeded);
                var failed = Assert.Single(looseActivities);
                Assert.False(failed.Ok);
                Assert.Equal(checkout.Error, failed.Error);
            }
            finally { Delete(loose); }

            var repo = RepoWithModelChangeBranch("git-public-unsaved-");
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Free(), repo.Dir);
                await engine.OpenAsync(repo.Model);
                var activities = new List<EngineActivityEvent>();
                var modelChanges = 0;
                sessions.Bus.Activity += activities.Add;

                var ambiguous = await McpTools.GitBranch(engine, "model-change");
                Assert.False(ambiguous.Ok);
                Assert.Contains("create=true", ambiguous.Error, StringComparison.Ordinal);
                Assert.Empty(activities);

                var missingName = await McpTools.GitBranch(engine, null, create: true);
                Assert.False(missingName.Ok);
                Assert.Contains("branch name", missingName.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Single(activities);
                Assert.False(activities[0].Ok);

                var optionInjection = await McpTools.GitCheckout(engine, "--orphan");
                Assert.False(optionInjection.Ok);
                Assert.Contains("not valid", optionInjection.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(2, activities.Count);

                File.WriteAllText(repo.Model, "dirty tracked model that a pathspec checkout must not overwrite");
                var pathspecBefore = File.ReadAllBytes(repo.Model);
                var headBeforePathspec = Git(repo.Dir, "rev-parse", "HEAD");
                var pathspec = await McpTools.GitCheckout(engine, "model.bim");
                Assert.False(pathspec.Ok);
                Assert.Contains("does not resolve", pathspec.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(pathspecBefore, File.ReadAllBytes(repo.Model));
                Assert.Equal(headBeforePathspec, Git(repo.Dir, "rev-parse", "HEAD"));

                var branchPathspec = await McpTools.GitBranch(engine, "model.bim", checkout: true);
                Assert.False(branchPathspec.Ok);
                Assert.Contains("does not resolve", branchPathspec.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(pathspecBefore, File.ReadAllBytes(repo.Model));

                var measure = (await engine.ListMeasuresAsync()).First();
                await engine.SetDaxAsync(measure.Ref, "1", "human");
                var diskBefore = File.ReadAllBytes(repo.Model);
                var headBefore = Git(repo.Dir, "rev-parse", "HEAD");
                sessions.Bus.Changed += _ => modelChanges++;

                var checkout = await McpTools.GitCheckout(engine, "model-change");
                Assert.False(checkout.Ok);
                Assert.Contains("unsaved edits", checkout.Error, StringComparison.OrdinalIgnoreCase);
                Assert.False(checkout.ModelReloadNeeded);
                Assert.Equal(headBefore, Git(repo.Dir, "rev-parse", "HEAD"));
                Assert.Equal(diskBefore, File.ReadAllBytes(repo.Model));
                Assert.Equal(5, activities.Count);
                Assert.Equal(0, modelChanges);
            }
            finally { Delete(repo.Root); }
        }

        [Fact]
        public async Task Checkout_failure_preserves_a_conflicting_dirty_worktree_byte_for_byte()
        {
            var repo = RepoWithModelChangeBranch("git-public-dirty-");
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Free(), repo.Dir);
                await engine.OpenAsync(repo.Model);
                File.WriteAllText(repo.Model, "local dirty model that must survive");
                var before = File.ReadAllBytes(repo.Model);
                var head = Git(repo.Dir, "rev-parse", "HEAD");
                var activities = new List<EngineActivityEvent>();
                sessions.Bus.Activity += activities.Add;

                var result = await McpTools.GitCheckout(engine, "model-change");

                Assert.False(result.Ok);
                Assert.False(result.ModelReloadNeeded);
                Assert.Equal(head, Git(repo.Dir, "rev-parse", "HEAD"));
                Assert.Equal(before, File.ReadAllBytes(repo.Model));
                var activity = Assert.Single(activities);
                Assert.False(activity.Ok);
                Assert.Equal(result.Error, activity.Error);
            }
            finally { Delete(repo.Root); }
        }

        [Fact]
        public async Task Pull_fast_forwards_locally_noop_is_honest_and_conflict_preserves_worktree()
        {
            var remote = RemoteFixture();
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Free(), remote.Work);
                await engine.OpenAsync(remote.WorkModel);
                var activities = new List<EngineActivityEvent>();
                var modelChanges = 0;
                sessions.Bus.Activity += activities.Add;
                sessions.Bus.Changed += _ => modelChanges++;

                File.AppendAllText(remote.PeerModel, "\n ");
                Git(remote.Peer, "add", "--", "model.bim");
                Git(remote.Peer, "commit", "-q", "-m", "remote model change");
                Git(remote.Peer, "push", "-q");

                var pulled = await McpTools.GitPull(engine);
                Assert.True(pulled.Ok, pulled.Error);
                Assert.True(pulled.ModelReloadNeeded);
                Assert.Equal(Path.GetFullPath(remote.WorkModel), pulled.ModelPath);
                Assert.Equal("main", pulled.Branch);

                var noop = await McpTools.GitPull(engine);
                Assert.True(noop.Ok, noop.Error);
                Assert.False(noop.ModelReloadNeeded);
                Assert.Null(noop.ModelPath);
                Assert.Equal("Already up to date", activities.Last().Label);

                File.AppendAllText(remote.PeerModel, "\n  ");
                Git(remote.Peer, "add", "--", "model.bim");
                Git(remote.Peer, "commit", "-q", "-m", "second remote model change");
                Git(remote.Peer, "push", "-q");
                File.WriteAllText(remote.WorkModel, "local dirty model that pull must preserve");
                var worktreeBefore = File.ReadAllBytes(remote.WorkModel);
                var headBefore = Git(remote.Work, "rev-parse", "HEAD");

                var refused = await McpTools.GitPull(engine);
                Assert.False(refused.Ok);
                Assert.False(refused.ModelReloadNeeded);
                Assert.Equal(headBefore, Git(remote.Work, "rev-parse", "HEAD"));
                Assert.Equal(worktreeBefore, File.ReadAllBytes(remote.WorkModel));
                Assert.Equal(3, activities.Count);
                Assert.False(activities[2].Ok);
                Assert.Equal(refused.Error, activities[2].Error);
                Assert.Equal(0, modelChanges);
            }
            finally { Delete(remote.Root); }
        }

        [Fact]
        public async Task Clone_resolves_relative_to_workspace_and_never_publishes_a_partial_target()
        {
            var source = RepoWithModelChangeBranch("git-public-clone-source-");
            var workspace = Path.Combine(Path.GetTempPath(), "git-public-clone-workspace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Free(), workspace);
                var activities = new List<EngineActivityEvent>();
                var modelChanges = 0;
                sessions.Bus.Activity += activities.Add;
                sessions.Bus.Changed += _ => modelChanges++;

                var cloned = await McpTools.GitClone(engine, source.Dir, "cloned");
                var cloneDir = Path.Combine(workspace, "cloned");
                Assert.True(cloned.Ok, cloned.Error);
                Assert.Equal("main", cloned.Branch);
                Assert.Equal(Path.Combine(cloneDir, "model.bim"), cloned.ModelPath);
                Assert.True(File.Exists(cloned.ModelPath));

                var failedTarget = Path.Combine(workspace, "failed");
                var missing = await McpTools.GitClone(engine, Path.Combine(workspace, "missing-source"), "failed");
                Assert.False(missing.Ok);
                Assert.False(Directory.Exists(failedTarget));
                Assert.Empty(Directory.EnumerateDirectories(workspace, ".semanticus-clone-*"));

                var existing = Path.Combine(workspace, "existing");
                Directory.CreateDirectory(existing);
                var sentinel = Path.Combine(existing, "keep.txt");
                File.WriteAllText(sentinel, "keep");
                var overwrite = await McpTools.GitClone(engine, source.Dir, "existing");
                Assert.False(overwrite.Ok);
                Assert.Equal("keep", File.ReadAllText(sentinel));

                var escape = await McpTools.GitClone(engine, source.Dir, Path.Combine("..", "outside-workspace"));
                Assert.False(escape.Ok);
                Assert.Contains("stay inside", escape.Error, StringComparison.OrdinalIgnoreCase);

                var option = await McpTools.GitClone(engine, "--mirror", "option-injection");
                Assert.False(option.Ok);
                Assert.False(Directory.Exists(Path.Combine(workspace, "option-injection")));

                Assert.Equal(5, activities.Count);
                Assert.True(activities[0].Ok);
                Assert.All(activities.Skip(1), activity => Assert.False(activity.Ok));
                Assert.All(activities, activity => Assert.Equal("git_clone", activity.Kind));
                Assert.Equal(0, modelChanges);
            }
            finally
            {
                Delete(source.Root);
                Delete(workspace);
            }
        }

        [Fact]
        public void Every_git_error_shape_scrubs_password_and_username_only_url_credentials()
        {
            var run = new GitCli.GitRun
            {
                ExitCode = 128,
                Stdout = "retry https://ghp_username_only@example.invalid/one",
                Stderr = "fatal: unable to access 'https://alice:super-secret@example.invalid/repo?access_token=abc.def.ghi': refused",
            };
            var output = GitCli.Combine(run);
            var error = GitCli.Error(run);

            Assert.DoesNotContain("alice:super-secret", output, StringComparison.Ordinal);
            Assert.DoesNotContain("ghp_username_only", output, StringComparison.Ordinal);
            Assert.DoesNotContain("abc.def.ghi", output, StringComparison.Ordinal);
            Assert.Contains("https://***@example.invalid", output, StringComparison.Ordinal);
            Assert.Contains("access_token=***", output, StringComparison.Ordinal);
            Assert.DoesNotContain("alice:super-secret", error, StringComparison.Ordinal);
            Assert.DoesNotContain("abc.def.ghi", error, StringComparison.Ordinal);

            var malformed = GitCli.Scrub("fatal: unable to access 'https://alice:sec/ret@part@example.invalid/repo': rejected");
            Assert.DoesNotContain("alice:sec/ret", malformed, StringComparison.Ordinal);
            Assert.DoesNotContain("part@", malformed, StringComparison.Ordinal);
            Assert.Contains("https://***@example.invalid", malformed, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Checkout_holds_the_clean_session_gate_and_rejects_a_concurrent_edit_and_save()
        {
            var repo = RepoWithModelChangeBranch("git-public-checkout-race-");
            var replacement = RepoWithModelChangeBranch("git-public-checkout-replacement-");
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Free(), repo.Dir);
                await engine.OpenAsync(repo.Model);
                var measure = (await engine.ListMeasuresAsync()).First();
                var revision = sessions.Current.Revision;
                var replacementBytes = File.ReadAllBytes(replacement.Model);
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var changed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var releaseChanged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                engine.GitStateChangeReadyForTest = async () =>
                {
                    ready.TrySetResult(true);
                    await release.Task;
                };
                engine.GitStateChangedForTest = async () =>
                {
                    changed.TrySetResult(true);
                    await releaseChanged.Task;
                };

                var checkoutTask = McpTools.GitCheckout(engine, "model-change");
                Task<OpenResult> openTask = null;
                try
                {
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    openTask = engine.OpenAsync(replacement.Model);
                    Assert.False(openTask.IsCompleted);

                    var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SetDaxAsync(measure.Ref, "42", "human"));
                    Assert.Contains("source control", refused.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(revision, sessions.Current.Revision);

                    release.TrySetResult(true);
                    await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var switchedBytes = File.ReadAllBytes(repo.Model);
                    var refusedSave = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SaveAsync(null, "BIM"));
                    Assert.Contains("cannot be saved", refusedSave.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(switchedBytes, File.ReadAllBytes(repo.Model));
                    Assert.Equal(revision, sessions.Current.Revision);
                }
                finally
                {
                    release.TrySetResult(true);
                    releaseChanged.TrySetResult(true);
                }

                var checkout = await checkoutTask;
                Assert.True(checkout.Ok, checkout.Error);
                Assert.True(checkout.ModelReloadNeeded);
                Assert.Equal("model-change", Git(repo.Dir, "branch", "--show-current"));

                await openTask;
                Assert.Equal(Path.GetFullPath(replacement.Model), Path.GetFullPath(sessions.Current.SourcePath));
                Assert.Equal("main", Git(replacement.Dir, "branch", "--show-current"));
                Assert.Equal(replacementBytes, File.ReadAllBytes(replacement.Model));
            }
            finally
            {
                Delete(repo.Root);
                Delete(replacement.Root);
            }
        }

        [Fact]
        public async Task Pull_holds_the_clean_session_gate_and_rejects_a_concurrent_edit()
        {
            var remote = RemoteFixture();
            try
            {
                File.AppendAllText(remote.PeerModel, "\n ");
                Git(remote.Peer, "add", "--", "model.bim");
                Git(remote.Peer, "commit", "-q", "-m", "remote model change for race");
                Git(remote.Peer, "push", "-q");

                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Free(), remote.Work);
                await engine.OpenAsync(remote.WorkModel);
                var measure = (await engine.ListMeasuresAsync()).First();
                var revision = sessions.Current.Revision;
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                engine.GitStateChangeReadyForTest = async () =>
                {
                    ready.TrySetResult(true);
                    await release.Task;
                };

                var pullTask = McpTools.GitPull(engine);
                try
                {
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SetDaxAsync(measure.Ref, "42", "agent"));
                    Assert.Contains("source control", refused.Message, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(revision, sessions.Current.Revision);
                }
                finally { release.TrySetResult(true); }

                var pull = await pullTask;
                Assert.True(pull.Ok, pull.Error);
                Assert.True(pull.ModelReloadNeeded);
                Assert.Equal(revision, sessions.Current.Revision);
            }
            finally { Delete(remote.Root); }
        }

        [Fact]
        public async Task Edit_that_overlaps_a_completed_checkout_is_rejected_as_stale()
        {
            var repo = RepoWithModelChangeBranch("git-public-stale-edit-");
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Free(), repo.Dir);
                await engine.OpenAsync(repo.Model);
                var measure = (await engine.ListMeasuresAsync()).First();
                var originalDax = measure.Expression;
                var revision = sessions.Current.Revision;
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                sessions.Current.ModelStateLeaseReadyForTest = async () =>
                {
                    ready.TrySetResult(true);
                    await release.Task;
                };

                var editTask = engine.SetDaxAsync(measure.Ref, "42", "human");
                try
                {
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    var checkout = await McpTools.GitCheckout(engine, "model-change");
                    Assert.True(checkout.Ok, checkout.Error);
                    Assert.True(checkout.ModelReloadNeeded);
                    Assert.Equal("model-change", Git(repo.Dir, "branch", "--show-current"));
                }
                finally { release.TrySetResult(true); }

                var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => editTask);
                Assert.Contains("source control", refused.Message, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(revision, sessions.Current.Revision);
                Assert.Equal(originalDax, (await engine.ListMeasuresAsync()).Single(x => x.Ref == measure.Ref).Expression);
            }
            finally { Delete(repo.Root); }
        }

        [Fact]
        public async Task Edit_that_owns_the_gate_before_checkout_intent_finishes_first()
        {
            var repo = RepoWithModelChangeBranch("git-public-edit-wins-");
            try
            {
                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Free(), repo.Dir);
                await engine.OpenAsync(repo.Model);
                var measure = (await engine.ListMeasuresAsync()).First();
                var revision = sessions.Current.Revision;
                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                sessions.Current.ModelStateLeaseAcquiredForTest = async () =>
                {
                    ready.TrySetResult(true);
                    await release.Task;
                };

                var editTask = engine.SetDaxAsync(measure.Ref, "42", "human");
                Task<GitActionResult> checkoutTask = null;
                try
                {
                    await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    checkoutTask = McpTools.GitCheckout(engine, "model-change");
                    Assert.False(checkoutTask.IsCompleted);
                }
                finally { release.TrySetResult(true); }

                Assert.True((await editTask).Changed);
                Assert.Equal(revision + 1, sessions.Current.Revision);
                Assert.Equal("42", (await engine.ListMeasuresAsync()).Single(x => x.Ref == measure.Ref).Expression);
                var checkout = await checkoutTask;
                Assert.False(checkout.Ok);
                Assert.Contains("unsaved edits", checkout.Error, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("main", Git(repo.Dir, "branch", "--show-current"));
            }
            finally { Delete(repo.Root); }
        }

        [Fact]
        public async Task Clone_refuses_a_relative_target_through_a_workspace_link()
        {
            var source = RepoWithModelChangeBranch("git-public-clone-link-source-");
            var workspace = Path.Combine(Path.GetTempPath(), "git-public-clone-link-workspace-" + Guid.NewGuid().ToString("N"));
            var outside = Path.Combine(Path.GetTempPath(), "git-public-clone-link-outside-" + Guid.NewGuid().ToString("N"));
            var link = Path.Combine(workspace, "linked");
            Directory.CreateDirectory(workspace);
            Directory.CreateDirectory(outside);
            try
            {
                try { Directory.CreateSymbolicLink(link, outside); }
                catch (Exception ex) when (OperatingSystem.IsWindows() &&
                    (ex is UnauthorizedAccessException || ex is IOException || ex is PlatformNotSupportedException))
                {
                    return; // Ubuntu CI always exercises the real link; some Windows hosts deny link creation.
                }

                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Free(), workspace);
                var activities = new List<EngineActivityEvent>();
                sessions.Bus.Activity += activities.Add;

                var result = await McpTools.GitClone(engine, source.Dir, Path.Combine("linked", "escaped"));

                Assert.False(result.Ok);
                Assert.Contains("linked workspace directory", result.Error, StringComparison.OrdinalIgnoreCase);
                Assert.False(Directory.Exists(Path.Combine(outside, "escaped")));
                Assert.Empty(Directory.EnumerateDirectories(workspace, ".semanticus-clone-*"));
                var activity = Assert.Single(activities);
                Assert.False(activity.Ok);
                Assert.Equal(result.Error, activity.Error);
            }
            finally
            {
                try { if (Directory.Exists(link)) Directory.Delete(link); } catch { }
                Delete(source.Root);
                Delete(workspace);
                Delete(outside);
            }
        }

        [Fact]
        public async Task Clone_refuses_a_relative_target_when_the_workspace_root_is_linked()
        {
            var source = RepoWithModelChangeBranch("git-public-clone-linked-root-source-");
            var root = Path.Combine(Path.GetTempPath(), "git-public-clone-linked-root-" + Guid.NewGuid().ToString("N"));
            var outside = Path.Combine(root, "outside");
            var workspace = Path.Combine(root, "workspace-link");
            Directory.CreateDirectory(outside);
            try
            {
                try { Directory.CreateSymbolicLink(workspace, outside); }
                catch (Exception ex) when (OperatingSystem.IsWindows() &&
                    (ex is UnauthorizedAccessException || ex is IOException || ex is PlatformNotSupportedException))
                {
                    return; // Ubuntu CI always exercises the real link; some Windows hosts deny link creation.
                }

                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Free(), workspace);
                var activities = new List<EngineActivityEvent>();
                sessions.Bus.Activity += activities.Add;

                var result = await McpTools.GitClone(engine, source.Dir, "escaped");

                Assert.False(result.Ok);
                Assert.Contains("linked workspace directory", result.Error, StringComparison.OrdinalIgnoreCase);
                Assert.False(Directory.Exists(Path.Combine(outside, "escaped")));
                Assert.Empty(Directory.EnumerateDirectories(outside, ".semanticus-clone-*"));
                var activity = Assert.Single(activities);
                Assert.False(activity.Ok);
                Assert.Equal(result.Error, activity.Error);
            }
            finally
            {
                try { if (Directory.Exists(workspace)) Directory.Delete(workspace); } catch { }
                Delete(source.Root);
                Delete(root);
            }
        }

        [Fact]
        public async Task Clone_refuses_a_linked_pbip_definition_before_publication_and_preserves_external_files()
        {
            if (OperatingSystem.IsWindows()) return; // Ubuntu CI exercises the Git symlink checkout deterministically.

            var root = Path.Combine(Path.GetTempPath(), "git-public-clone-pbip-link-" + Guid.NewGuid().ToString("N"));
            var source = Path.Combine(root, "source");
            var outside = Path.Combine(root, "outside-definition");
            var workspace = Path.Combine(root, "workspace");
            var modelFolder = Path.Combine(source, "Linked.SemanticModel");
            var definitionLink = Path.Combine(modelFolder, "definition");
            var sentinel = Path.Combine(outside, "sentinel.txt");
            Directory.CreateDirectory(modelFolder);
            Directory.CreateDirectory(outside);
            Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(modelFolder, "definition.pbism"), "{}");
            File.WriteAllText(Path.Combine(outside, "model.tmdl"), "model Model\n");
            File.WriteAllText(sentinel, "external file must remain untouched");
            Directory.CreateSymbolicLink(definitionLink, outside);
            try
            {
                Git(source, "init", "-q");
                ConfigureIdentity(source);
                Git(source, "add", "--", ".");
                Git(source, "commit", "-q", "-m", "linked PBIP definition");
                Git(source, "branch", "-M", "main");

                var sessions = new SessionManager();
                using var engine = new LocalEngine(sessions, new Free(), workspace);
                var activities = new List<EngineActivityEvent>();
                sessions.Bus.Activity += activities.Add;

                var target = Path.Combine(workspace, "cloned");
                var result = await McpTools.GitClone(engine, source, "cloned");

                Assert.False(result.Ok);
                Assert.Contains("linked repository path", result.Error, StringComparison.OrdinalIgnoreCase);
                Assert.False(Directory.Exists(target));
                Assert.Empty(Directory.EnumerateDirectories(workspace, ".semanticus-clone-*"));
                Assert.Equal("external file must remain untouched", File.ReadAllText(sentinel));
                var activity = Assert.Single(activities);
                Assert.False(activity.Ok);
                Assert.Equal(result.Error, activity.Error);
            }
            finally
            {
                try { if (Directory.Exists(definitionLink)) Directory.Delete(definitionLink); } catch { }
                Delete(root);
            }
        }

        [Fact]
        public void Clone_model_discovery_refuses_paths_outside_staging_or_through_a_link()
        {
            var root = Path.Combine(Path.GetTempPath(), "git-public-clone-model-path-" + Guid.NewGuid().ToString("N"));
            var staging = Path.Combine(root, "staging");
            var outside = Path.Combine(root, "outside");
            var link = Path.Combine(staging, "linked");
            var pbip = Path.Combine(staging, "Linked.SemanticModel");
            var definitionLink = Path.Combine(pbip, "definition");
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(outside);
            var outsideModel = Path.Combine(outside, "model.bim");
            File.Copy(TestModels.FindBim(), outsideModel);
            try
            {
                var escaped = Assert.Throws<InvalidOperationException>(() => LocalEngine.CloneModelRelativePath(staging, outsideModel));
                Assert.Contains("outside the cloned repository", escaped.Message, StringComparison.OrdinalIgnoreCase);

                try { Directory.CreateSymbolicLink(link, outside); }
                catch (Exception ex) when (OperatingSystem.IsWindows() &&
                    (ex is UnauthorizedAccessException || ex is IOException || ex is PlatformNotSupportedException))
                {
                    return; // Ubuntu CI always exercises the real link; some Windows hosts deny link creation.
                }

                var linked = Assert.Throws<InvalidOperationException>(() => LocalEngine.CloneModelRelativePath(staging, Path.Combine(link, "model.bim")));
                Assert.Contains("linked repository path", linked.Message, StringComparison.OrdinalIgnoreCase);

                Directory.CreateDirectory(pbip);
                File.WriteAllText(Path.Combine(pbip, "definition.pbism"), "{}");
                Directory.CreateSymbolicLink(definitionLink, outside);
                var linkedDefinition = Assert.Throws<InvalidOperationException>(() => LocalEngine.CloneModelRelativePath(staging, pbip));
                Assert.Contains("linked repository path", linkedDefinition.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                try { if (Directory.Exists(definitionLink)) Directory.Delete(definitionLink); } catch { }
                try { if (Directory.Exists(link)) Directory.Delete(link); } catch { }
                Delete(root);
            }
        }

        private static (string Root, string Dir, string Model) RepoWithModelChangeBranch(string prefix)
        {
            var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
            var dir = Path.Combine(root, "work");
            Directory.CreateDirectory(dir);
            var model = Path.Combine(dir, "model.bim");
            File.Copy(TestModels.FindBim(), model);
            Git(dir, "init", "-q");
            ConfigureIdentity(dir);
            Git(dir, "add", "--", "model.bim");
            Git(dir, "commit", "-q", "-m", "initial");
            Git(dir, "branch", "-M", "main");
            Git(dir, "checkout", "-q", "-b", "model-change");
            File.AppendAllText(model, "\n ");
            Git(dir, "add", "--", "model.bim");
            Git(dir, "commit", "-q", "-m", "model change");
            Git(dir, "checkout", "-q", "main");
            return (root, dir, model);
        }

        private static (string Root, string Origin, string Work, string WorkModel, string Peer, string PeerModel) RemoteFixture()
        {
            var root = Path.Combine(Path.GetTempPath(), "git-public-pull-" + Guid.NewGuid().ToString("N"));
            var origin = Path.Combine(root, "origin.git");
            Directory.CreateDirectory(root);
            Git(root, "init", "-q", "--bare", origin);
            var seed = RepoWithModelChangeBranch("git-public-pull-seed-");
            var work = Path.Combine(root, "work");
            Directory.Move(seed.Dir, work);
            Delete(seed.Root);
            Git(work, "remote", "add", "origin", origin);
            Git(work, "push", "-q", "-u", "origin", "main");
            Git(origin, "symbolic-ref", "HEAD", "refs/heads/main");
            var peer = Path.Combine(root, "peer");
            Git(root, "clone", "-q", origin, peer);
            ConfigureIdentity(peer);
            return (root, origin, work, Path.Combine(work, "model.bim"), peer, Path.Combine(peer, "model.bim"));
        }

        private static void ConfigureIdentity(string dir)
        {
            Git(dir, "config", "user.email", "git-public@test.local");
            Git(dir, "config", "user.name", "Git Public Test");
            Git(dir, "config", "commit.gpgsign", "false");
        }

        private static string Git(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException(stderr);
            return stdout.Trim();
        }

        private static void Delete(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                Directory.Delete(dir, recursive: true);
            }
            catch { }
        }
    }
}
