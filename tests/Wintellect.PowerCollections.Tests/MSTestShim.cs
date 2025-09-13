// MSTest to xUnit compatibility shim to minimize code changes
using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Microsoft.VisualStudio.TestTools.UnitTesting
{
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
    public sealed class TestClassAttribute : Attribute { }

    // Use a custom discoverer so we can honor [ExpectedException]
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    [XunitTestCaseDiscoverer("Wintellect.PowerCollections.Tests.MSTestShim.TestMethodDiscoverer", "Wintellect.PowerCollections.Tests")]
    public class TestMethodAttribute : Xunit.FactAttribute { }

    // Some tests may use these lifecycle attributes; treat them as no-ops under xUnit
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class TestInitializeAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class TestCleanupAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class ClassInitializeAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class ClassCleanupAttribute : Attribute { }

    // Minimal reflection helpers to support legacy VS CodeGen Accessors
    public sealed class PrivateType
    {
        private readonly Type _type;
        private readonly System.Reflection.Assembly _assembly;

        public PrivateType(string assemblyName, string typeName)
        {
            _assembly = System.Reflection.Assembly.Load(assemblyName);
            _type = _assembly.GetType(typeName, throwOnError: true)!;
        }

        public object? GetStaticField(string name)
        {
            var field = _type.GetField(name, System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
            return field?.GetValue(null);
        }
    }

    public sealed class PrivateObject
    {
        public object? Target { get; }
        public PrivateType PrivateType { get; }

        public PrivateObject(object? target, PrivateType type)
        {
            Target = target;
            PrivateType = type;
        }
    }

    // Map common MSTest Assert methods to xUnit Assert
    public static class Assert
    {
        public static void IsTrue(bool condition, string? message = null) => Xunit.Assert.True(condition, message);
        public static void IsFalse(bool condition, string? message = null) => Xunit.Assert.False(condition, message);
        public static void AreEqual<T>(T expected, T actual) => Xunit.Assert.Equal(expected, actual);
        public static void AreEqual<T>(T expected, T actual, string? message) => Xunit.Assert.Equal(expected, actual);
        public static void AreNotEqual<T>(T notExpected, T actual) => Xunit.Assert.NotEqual(notExpected, actual);
        public static void AreNotEqual<T>(T notExpected, T actual, string? message) => Xunit.Assert.NotEqual(notExpected, actual);
        public static void AreSame(object? expected, object? actual) => Xunit.Assert.Same(expected, actual);
        public static void AreNotSame(object? notExpected, object? actual) => Xunit.Assert.NotSame(notExpected, actual);
        public static void IsNull(object? value, string? message = null) => Xunit.Assert.Null(value);
        public static void IsNotNull(object? value, string? message = null) => Xunit.Assert.NotNull(value);
        public static void Fail(string? message = null) => Xunit.Assert.True(false, message ?? "Assert.Fail was called");
        public static void Inconclusive(string? message = null) => throw new XunitException(message ?? "Test Inconclusive");
    }

    // [ExpectedException] metadata used by our discoverer to enforce behavior at runtime
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public sealed class ExpectedExceptionAttribute : Attribute
    {
        public Type ExceptionType { get; }
        public string? Message { get; }

        public ExpectedExceptionAttribute(Type exceptionType)
        {
            ExceptionType = exceptionType;
        }

        public ExpectedExceptionAttribute(Type exceptionType, string message)
        {
            ExceptionType = exceptionType;
            Message = message;
        }
    }
}

namespace Wintellect.PowerCollections.Tests.MSTestShim
{
    using Microsoft.VisualStudio.TestTools.UnitTesting;

    // Discoverer that returns a special test case when [ExpectedException] is present
    internal sealed class TestMethodDiscoverer : IXunitTestCaseDiscoverer
    {
        private readonly IMessageSink diagnosticMessageSink;
        public TestMethodDiscoverer(IMessageSink diagnosticMessageSink)
        {
            this.diagnosticMessageSink = diagnosticMessageSink;
        }

        public IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute)
        {
            var expectedAttrName = typeof(ExpectedExceptionAttribute).AssemblyQualifiedName ?? typeof(ExpectedExceptionAttribute).FullName;
            var expected = testMethod.Method.GetCustomAttributes(expectedAttrName).FirstOrDefault();
            if (expected != null)
            {
                var ctorArgs = expected.GetConstructorArguments().ToList();
                var exceptionType = (Type)ctorArgs[0]!;
                string? message = ctorArgs.Count > 1 ? ctorArgs[1] as string : null;
                yield return new ExpectedExceptionTestCase(diagnosticMessageSink,
                    discoveryOptions.MethodDisplayOrDefault(),
                    discoveryOptions.MethodDisplayOptionsOrDefault(),
                    testMethod, exceptionType, message);
                yield break;
            }

            yield return new XunitTestCase(diagnosticMessageSink,
                discoveryOptions.MethodDisplayOrDefault(),
                discoveryOptions.MethodDisplayOptionsOrDefault(),
                testMethod);
        }
    }

    internal sealed class ExpectedExceptionTestCase : XunitTestCase
    {
        private Type? expectedType;
        private string? expectedMessage;

        // For de-serialization
        public ExpectedExceptionTestCase() { }

        public ExpectedExceptionTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay, TestMethodDisplayOptions defaultMethodDisplayOptions, ITestMethod testMethod, Type expectedType, string? expectedMessage)
            : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod)
        {
            this.expectedType = expectedType;
            this.expectedMessage = expectedMessage;
        }

        public override void Serialize(IXunitSerializationInfo data)
        {
            base.Serialize(data);
            data.AddValue("expectedType", expectedType!.AssemblyQualifiedName);
            data.AddValue("expectedMessage", expectedMessage);
        }

        public override void Deserialize(IXunitSerializationInfo data)
        {
            base.Deserialize(data);
            var typeName = data.GetValue<string>("expectedType");
            expectedType = Type.GetType(typeName, throwOnError: true);
            expectedMessage = data.GetValue<string>("expectedMessage");
        }

        public override Task<RunSummary> RunAsync(IMessageSink diagnosticMessageSink, IMessageBus messageBus, object[] constructorArguments, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
        {
            return new ExpectedExceptionTestCaseRunner(this, DisplayName, SkipReason, constructorArguments, messageBus, aggregator, cancellationTokenSource, expectedType!, expectedMessage).RunAsync();
        }
    }

    internal sealed class ExpectedExceptionTestCaseRunner : XunitTestCaseRunner
    {
        private readonly Type expectedType;
        private readonly string? expectedMessage;

        public ExpectedExceptionTestCaseRunner(IXunitTestCase testCase, string displayName, string? skipReason, object[] constructorArguments, IMessageBus messageBus, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource, Type expectedType, string? expectedMessage)
            : base(testCase, displayName, skipReason, constructorArguments, new object?[0], messageBus, aggregator, cancellationTokenSource)
        {
            this.expectedType = expectedType;
            this.expectedMessage = expectedMessage;
        }

        protected override Task<RunSummary> RunTestAsync()
        {
            return new ExpectedExceptionTestRunner(new XunitTest(TestCase, DisplayName), MessageBus, TestClass, ConstructorArguments, TestMethod, TestMethodArguments, SkipReason, BeforeAfterAttributes, Aggregator, CancellationTokenSource, expectedType, expectedMessage).RunAsync();
        }

        private static Exception Unwrap(Exception ex)
        {
            if (ex is TargetInvocationException tie && tie.InnerException != null)
                return tie.InnerException;
            return ex;
        }
    }

    internal sealed class ExpectedExceptionTestRunner : XunitTestRunner
    {
        private readonly Type expectedType;
        private readonly string? expectedMessage;

        public ExpectedExceptionTestRunner(ITest test, IMessageBus messageBus, Type testClass, object[] constructorArguments, MethodInfo testMethod, object[] testMethodArguments, string? skipReason, IReadOnlyList<BeforeAfterTestAttribute> beforeAfterAttributes, ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource, Type expectedType, string? expectedMessage)
            : base(test, messageBus, testClass, constructorArguments, testMethod, testMethodArguments, skipReason, beforeAfterAttributes, aggregator, cancellationTokenSource)
        {
            this.expectedType = expectedType;
            this.expectedMessage = expectedMessage;
        }

        protected override async Task<decimal> InvokeTestMethodAsync(ExceptionAggregator aggregator)
        {
            var elapsed = await base.InvokeTestMethodAsync(aggregator);

            if (aggregator.HasExceptions)
            {
                var ex = aggregator.ToException();
                var thrown = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                if (expectedType.GetTypeInfo().IsAssignableFrom(thrown.GetType()))
                {
                    if (expectedMessage != null && !string.Equals(thrown.Message, expectedMessage, StringComparison.Ordinal))
                    {
                        // Wrong message: keep failure as-is, but replace with clearer one
                        aggregator.Clear();
                        aggregator.Add(new XunitException($"Expected exception message '{expectedMessage}', but was '{thrown.Message}'."));
                    }
                    else
                    {
                        // Correct exception: clear failure to mark success
                        aggregator.Clear();
                    }
                }
                else
                {
                    // Wrong type: replace with clearer failure
                    aggregator.Clear();
                    aggregator.Add(new XunitException($"Expected exception of type {expectedType.FullName}, but {thrown.GetType().FullName} was thrown."));
                }
            }
            else
            {
                aggregator.Add(new XunitException($"No exception thrown. Expected: {expectedType.FullName}"));
            }

            return elapsed;
        }
    }
}