using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: TestCaseOrderer("Semanticus.Tests.SeededTestOrderer", "Semanticus.Tests")]
[assembly: TestCollectionOrderer("Semanticus.Tests.SeededTestOrderer", "Semanticus.Tests")]
[assembly: TestFramework("Semanticus.Tests.NoBrowserTestFramework", "Semanticus.Tests")]

namespace Semanticus.Tests
{
    public sealed class SeededTestOrderer : ITestCaseOrderer, ITestCollectionOrderer
    {
        private static string DefaultSeed => OperatingSystem.IsWindows()
            ? "semanticus-hermetic-windows-v1"
            : "semanticus-hermetic-unix-v1";

        public IEnumerable<TTestCase> OrderTestCases<TTestCase>(IEnumerable<TTestCase> testCases)
            where TTestCase : ITestCase
            => testCases.OrderBy(test => Key(test.DisplayName), StringComparer.Ordinal);

        public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections)
            => testCollections.OrderBy(collection => Key(collection.DisplayName), StringComparer.Ordinal);

        private static string Key(string displayName)
        {
            var seed = Environment.GetEnvironmentVariable("SEMANTICUS_TEST_ORDER_SEED") ?? DefaultSeed;
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed + "\0" + displayName)));
        }
    }

    /// <summary>xUnit's own framework, plus one check after the last test of the run: if anything in the run called a
    /// browser or desktop stub (InteractiveSignIn.StubBrowsersForTestRun), the run fails, whichever test or process did
    /// it and whenever. A test that checked the stub log could only see what happened before it ran, and the other
    /// collections run beside it (round 4: a launch after that check left the suite green). The process-exit check the
    /// net also arms does not help here: dotnet test ignores the test host's exit code, but it fails a run that reports
    /// an error message.</summary>
    public sealed class NoBrowserTestFramework : XunitTestFramework
    {
        public NoBrowserTestFramework(IMessageSink messageSink) : base(messageSink) { }

        protected override ITestFrameworkExecutor CreateExecutor(AssemblyName assemblyName)
            => new Executor(assemblyName, SourceInformationProvider, DiagnosticMessageSink);

        private sealed class Executor : XunitTestFrameworkExecutor
        {
            public Executor(AssemblyName name, ISourceInformationProvider info, IMessageSink diagnostics) : base(name, info, diagnostics) { }

            protected override async void RunTestCases(IEnumerable<IXunitTestCase> testCases, IMessageSink executionMessageSink, ITestFrameworkExecutionOptions executionOptions)
            {
                using var runner = new Runner(TestAssembly, testCases, DiagnosticMessageSink, executionMessageSink, executionOptions);
                await runner.RunAsync();
            }
        }

        private sealed class Runner : XunitTestAssemblyRunner
        {
            public Runner(ITestAssembly assembly, IEnumerable<IXunitTestCase> testCases, IMessageSink diagnostics, IMessageSink execution, ITestFrameworkExecutionOptions options)
                : base(assembly, testCases, diagnostics, execution, options) { }

            protected override async System.Threading.Tasks.Task<RunSummary> RunTestCollectionsAsync(IMessageBus messageBus, System.Threading.CancellationTokenSource cancellationTokenSource)
            {
                var summary = await base.RunTestCollectionsAsync(messageBus, cancellationTokenSource);
                var report = Semanticus.Engine.InteractiveSignIn.LaunchAttemptReport();
                if (report != null)
                {
                    messageBus.QueueMessage(new ErrorMessage(TestCases, new[] { "Semanticus.Tests.NoBrowserNet" }, new[] { report }, new[] { "" }, new[] { -1 }));
                    summary.Failed++;
                    summary.Total++;
                }
                return summary;
            }
        }
    }

    internal static class TestEnvironment
    {
        [ModuleInitializer]
        internal static void Initialize()
        {
            // The production protocol is culture-neutral. Pin the harness too, so a developer's regional
            // settings cannot turn formatting, parsing, sorting, or snapshots into machine luck.
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

            // F-232: every per-user store (connections, the connection timeline, policy, approvals, restore
            // points) resolves to a scratch home for the whole run, and so does every engine process a test
            // starts. A fixture that forgets its own override can no longer write into the real
            // ~/.semanticus. Set before any test code runs, which is what a module initializer guarantees.
            Semanticus.Engine.SemanticusHome.IsolateForTestRun("tests");
        }
    }

    /// <summary>Named-pipe names for tests. On Unix a .NET named pipe IS a domain socket at
    /// "&lt;temp&gt;/CoreFxPipe_&lt;name&gt;", and that address has a hard 108-byte limit (107 usable, with the NUL) the framework reports as a raw
    /// ArgumentOutOfRangeException from inside a socket constructor, naming neither the test nor the pipe. The suite
    /// used to spend a whole 32-character guid on uniqueness, which is 24 of those 108 bytes for a name that only has
    /// to be unique within one test run; eight hex characters are, and several tests here already used eight. Mint
    /// here so no call site has to remember, and so a temp directory that genuinely cannot fit fails with the limit
    /// named rather than with the constructor's own words. See TestPipeNameTests.</summary>
    internal static class TestPipe
    {
        /// <summary>The most bytes a socket path may use. sun_path is 108 bytes and the path's terminating NUL takes
        /// one of them, so 108 itself fails to bind (measured: TestPipeNameTests pins both sides).</summary>
        internal const int UnixSocketPathLimit = 107;

        /// <summary>The address .NET opens on Unix for a pipe of this name under this temp directory.</summary>
        internal static string SocketPathFor(string tempDir, string name) =>
            System.IO.Path.Combine(tempDir, "CoreFxPipe_" + name);

        /// <summary>A unique pipe name for this run, short enough for the socket path this process would use.</summary>
        internal static string Mint(string prefix) => MintIn(System.IO.Path.GetTempPath(), prefix);

        /// <summary>The same, against a named temp directory, so the guard can test the rule without moving TMPDIR
        /// out from under tests running beside it.</summary>
        internal static string MintIn(string tempDir, string prefix)
        {
            var name = (prefix ?? "semanticus-") + Guid.NewGuid().ToString("N").Substring(0, 8);
            if (OperatingSystem.IsWindows()) return name;   // \\.\pipe\ has no such limit

            var path = SocketPathFor(tempDir, name);
            var bytes = Encoding.UTF8.GetByteCount(path);
            if (bytes <= UnixSocketPathLimit) return name;

            var dirBytes = Encoding.UTF8.GetByteCount(tempDir);
            throw new XunitException(
                $"a named pipe on this platform is a domain socket at '{path}', which is {bytes} bytes, and the limit "
                + $"is {UnixSocketPathLimit} (108 bytes of sun_path, less the terminating NUL). The temp directory alone spends {dirBytes} of them, and the shortest name "
                + $"this helper can mint still needs {bytes - dirBytes}. Point TMPDIR at a short directory (/tmp works) "
                + "and run again. This is the environment, not the test.");
        }
    }

    internal static class ReviewFenceTest
    {
        /// <summary>
        /// Mint the live review token without a preview round-trip. Tests that prove a refusal before any
        /// live connection cannot call deploy with commit=false: that path still talks to the target.
        /// </summary>
        internal static async System.Threading.Tasks.Task<string> TokenAsync(
            Semanticus.Engine.IEngine engine, string endpoint, string database, string origin = "human")
        {
            _ = origin;
            var info = await engine.SessionInfoAsync();
            var coords = Semanticus.Engine.ConnectionInput.Parse(endpoint, database);
            return Semanticus.Engine.ReviewFence.Mint(
                info.SessionId, info.Revision,
                Semanticus.Engine.ReviewFence.TargetIdentity(coords.Endpoint, coords.Database),
                Array.Empty<string>());
        }
    }

    internal static class CompiledCallGraph
    {
        private static readonly IReadOnlyDictionary<short, OpCode> OpCodesByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(OpCode))
            .Select(field => (OpCode)field.GetValue(null)!)
            .ToDictionary(code => code.Value);

        internal static IReadOnlyList<MethodInfo> FindCallers(Assembly assembly, MethodBase target)
            => assembly.GetTypes()
                .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                .Where(method => Calls(method, target))
                .ToArray();

        private static bool Calls(MethodInfo caller, MethodBase target) => CalleesOf(caller).Contains(target);

        /// <summary>Every method and constructor each method or constructor of <paramref name="assembly"/> calls or
        /// constructs, nested and compiler-made types (lambdas, async state machines) included.</summary>
        internal static IEnumerable<(MethodBase Caller, MethodBase Callee)> CallSites(Assembly assembly)
        {
            const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            return assembly.GetTypes()
                .SelectMany(type => type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
                .SelectMany(caller => CalleesOf(caller).Select(callee => (caller, callee)));
        }

        internal static IEnumerable<MethodBase> Callees(Assembly assembly) => CallSites(assembly).Select(c => c.Callee);

        private static IEnumerable<MethodBase> CalleesOf(MethodBase caller)
        {
            var il = caller.GetMethodBody()?.GetILAsByteArray();
            if (il == null) yield break;
            for (var offset = 0; offset < il.Length;)
            {
                short value = il[offset++];
                if (value == 0xfe) value = (short)(0xfe00 | il[offset++]);
                if (!OpCodesByValue.TryGetValue(value, out var op)) throw new InvalidOperationException($"Unknown IL opcode 0x{value:x4}");
                if (op.OperandType == OperandType.InlineMethod)
                {
                    var token = BitConverter.ToInt32(il, offset);
                    MethodBase? resolved = null;
                    try
                    {
                        resolved = caller.Module.ResolveMethod(
                            token,
                            caller.DeclaringType?.GetGenericArguments(),
                            caller.IsGenericMethod ? caller.GetGenericArguments() : null);
                    }
                    catch (ArgumentException)
                    {
                        // An unresolved optional reference cannot be the concrete target being pinned.
                    }
                    if (resolved != null) yield return resolved;
                }
                offset += OperandSize(op.OperandType, il, offset);
            }
        }

        private static int OperandSize(OperandType type, byte[] il, int offset) => type switch
        {
            OperandType.InlineNone => 0,
            OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
            OperandType.InlineVar => 2,
            OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI or OperandType.InlineMethod
                or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType
                or OperandType.ShortInlineR => 4,
            OperandType.InlineI8 or OperandType.InlineR => 8,
            OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, offset)),
            _ => throw new InvalidOperationException("Unsupported IL operand type: " + type),
        };
    }

    public sealed class TestEnvironmentContractTests
    {
        [Fact]
        public void Test_process_uses_invariant_culture()
        {
            Assert.Equal(CultureInfo.InvariantCulture, CultureInfo.CurrentCulture);
            Assert.Equal(CultureInfo.InvariantCulture, CultureInfo.CurrentUICulture);
            Assert.Equal(CultureInfo.InvariantCulture, CultureInfo.DefaultThreadCurrentCulture);
            Assert.Equal(CultureInfo.InvariantCulture, CultureInfo.DefaultThreadCurrentUICulture);
        }

        [Trait(TestCost.Key, TestCost.Sweep)]
        [Fact]
        public void Tests_never_read_the_machine_local_clock_or_timezone()
        {
            var forbidden = new MethodBase[]
            {
                typeof(DateTime).GetProperty(nameof(DateTime.Now))!.GetMethod!,
                typeof(DateTime).GetProperty(nameof(DateTime.Today))!.GetMethod!,
                typeof(DateTime).GetMethod(nameof(DateTime.ToLocalTime), Type.EmptyTypes)!,
                typeof(DateTimeOffset).GetProperty(nameof(DateTimeOffset.Now))!.GetMethod!,
                typeof(DateTimeOffset).GetProperty(nameof(DateTimeOffset.LocalDateTime))!.GetMethod!,
                typeof(DateTimeOffset).GetMethod(nameof(DateTimeOffset.ToLocalTime), Type.EmptyTypes)!,
                typeof(TimeZoneInfo).GetProperty(nameof(TimeZoneInfo.Local))!.GetMethod!,
            };
            var assembly = typeof(TestEnvironmentContractTests).Assembly;
            var callers = forbidden
                .SelectMany(target => CompiledCallGraph.FindCallers(assembly, target).Select(caller => caller.DeclaringType!.FullName + "." + caller.Name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            Assert.Empty(callers);
        }
    }
}
