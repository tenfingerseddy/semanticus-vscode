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

        private static bool Calls(MethodInfo caller, MethodBase target)
        {
            var il = caller.GetMethodBody()?.GetILAsByteArray();
            if (il == null) return false;
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
                    if (resolved == target) return true;
                }
                offset += OperandSize(op.OperandType, il, offset);
            }
            return false;
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
