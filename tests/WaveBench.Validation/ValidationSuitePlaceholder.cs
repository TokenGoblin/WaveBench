using Xunit;

namespace WaveBench.Validation;

/// <summary>
/// The §6.2 validation suite (published engine cases, dyno comparisons, …)
/// fills in from Phase 6 onward. Runs nightly and on release, not per-PR.
/// </summary>
public class ValidationSuitePlaceholder
{
    [Fact]
    [Trait("Category", "Validation")]
    public void Validation_project_is_wired_into_ci() => Assert.True(true);
}
