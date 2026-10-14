using Xunit;

namespace RCMM.Tests;

/// <summary>
/// Guards the runtime-skip machinery itself: if the discoverer or the
/// message-bus rewrite in SkippableFact.cs ever breaks, the first test below
/// turns into a FAILURE (SkipTestException surfacing as a normal exception)
/// instead of the expected Skipped result — so a broken skip mechanism can't
/// silently demote machine-dependent tests back to red or, worse, green.
/// </summary>
public class SkippableFactInfraTests
{
    [SkippableFact]
    public void A_runtime_skip_reports_as_skipped_not_failed()
    {
        Skip.If(true, "Intentional: verifies SkipTestException is rewritten to a Skipped result.");
        Assert.Fail("unreachable — the skip above must short-circuit the test");
    }

    [Fact]
    public void Skip_If_with_a_false_condition_does_not_throw()
    {
        Skip.If(false, "must not throw");
    }
}
