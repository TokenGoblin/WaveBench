using Xunit;

namespace WaveBench.Verification;

/// <summary>
/// The §6.1 verification suite (Riemann problems, order-of-accuracy, junction
/// coefficients, acoustic checks, …) fills in from Phase 2 onward. This
/// placeholder keeps the project wired into CI from Phase 0.
/// </summary>
public class VerificationSuitePlaceholder
{
    [Fact]
    public void Verification_project_is_wired_into_ci() => Assert.True(true);
}
