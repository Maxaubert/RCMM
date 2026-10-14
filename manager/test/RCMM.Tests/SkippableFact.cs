using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace RCMM.Tests;

// xUnit 2.x has no built-in runtime skip (Assert.Skip is v3-only, and pulling in
// Xunit.SkippableFact would add a NuGet dependency). Without this, a test whose
// preconditions aren't met on a given machine can only `return` early — which
// reports GREEN and makes "the assertions never ran" indistinguishable from "the
// assertions passed". This minimal implementation converts a SkipTestException
// thrown mid-test into a real Skipped result with a visible reason.

/// <summary>Marks a test that may skip itself at runtime via <see cref="Skip"/>.</summary>
[XunitTestCaseDiscoverer("RCMM.Tests.SkippableFactDiscoverer", "RCMM.Tests")]
[AttributeUsage(AttributeTargets.Method)]
public sealed class SkippableFactAttribute : FactAttribute { }

public sealed class SkipTestException : Exception
{
    public SkipTestException(string reason) : base(reason) { }
}

public static class Skip
{
    public static void If(bool condition, string reason)
    {
        if (condition) throw new SkipTestException(reason);
    }
}

public sealed class SkippableFactDiscoverer : IXunitTestCaseDiscoverer
{
    private readonly IMessageSink _diagnosticMessageSink;

    public SkippableFactDiscoverer(IMessageSink diagnosticMessageSink)
        => _diagnosticMessageSink = diagnosticMessageSink;

    public IEnumerable<IXunitTestCase> Discover(
        ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute)
    {
        yield return new SkippableFactTestCase(
            _diagnosticMessageSink,
            discoveryOptions.MethodDisplayOrDefault(),
            discoveryOptions.MethodDisplayOptionsOrDefault(),
            testMethod);
    }
}

public sealed class SkippableFactTestCase : XunitTestCase
{
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes")]
    public SkippableFactTestCase() { }

    public SkippableFactTestCase(
        IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay,
        TestMethodDisplayOptions defaultMethodDisplayOptions, ITestMethod testMethod,
        object[]? testMethodArguments = null)
        : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod, testMethodArguments) { }

    public override async Task<RunSummary> RunAsync(
        IMessageSink diagnosticMessageSink, IMessageBus messageBus, object[] constructorArguments,
        ExceptionAggregator aggregator, CancellationTokenSource cancellationTokenSource)
    {
        var skipBus = new SkipRewritingMessageBus(messageBus);
        var summary = await base.RunAsync(
            diagnosticMessageSink, skipBus, constructorArguments, aggregator, cancellationTokenSource);
        if (skipBus.SkippedCount > 0)
        {
            summary.Failed -= skipBus.SkippedCount;
            summary.Skipped += skipBus.SkippedCount;
        }
        return summary;
    }

    /// <summary>Rewrites a failure caused by SkipTestException into a Skipped
    /// result so the runner reports it in the Skipped column with the reason.</summary>
    private sealed class SkipRewritingMessageBus : IMessageBus
    {
        private readonly IMessageBus _inner;
        public int SkippedCount { get; private set; }

        public SkipRewritingMessageBus(IMessageBus inner) => _inner = inner;

        // The owning runner disposes the real bus; this wrapper owns nothing.
        public void Dispose() { }

        public bool QueueMessage(IMessageSinkMessage message)
        {
            if (message is ITestFailed failed
                && failed.ExceptionTypes.FirstOrDefault() == typeof(SkipTestException).FullName)
            {
                SkippedCount++;
                return _inner.QueueMessage(new TestSkipped(
                    failed.Test, failed.Messages.FirstOrDefault() ?? "Skipped"));
            }
            return _inner.QueueMessage(message);
        }
    }
}
