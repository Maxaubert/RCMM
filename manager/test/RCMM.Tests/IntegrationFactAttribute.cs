using System;
using Xunit;

namespace RCMM.Tests;

/// <summary>
/// A [Fact] that only runs when RCMM_RUN_INTEGRATION=1 is set. For tests that
/// cannot be sandboxed or that need an environment the plain `dotnet test` host
/// can't provide (e.g. live COM shell capture needs an Explorer-like STA
/// process). Skipping with a visible reason — instead of a permanent
/// [Fact(Skip=...)] or a silent early return — keeps the gap honest in the test
/// report while leaving the test runnable on demand.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class IntegrationFactAttribute : FactAttribute
{
    public const string EnvVar = "RCMM_RUN_INTEGRATION";

    public IntegrationFactAttribute(string? requirement = null)
    {
        if (Environment.GetEnvironmentVariable(EnvVar) != "1")
        {
            Skip = $"Integration test — set {EnvVar}=1 to run."
                 + (requirement is null ? "" : " " + requirement);
        }
    }
}
