using WaveBench.Core.Numerics;

namespace WaveBench.Core.Solver;

/// <summary>Ghost state prescribed by an external end boundary.</summary>
public readonly record struct EndGhost(PrimitiveState State, double[]? MassFractions);

/// <summary>An injector-style mass source into one duct cell (plan §2.7).</summary>
public sealed class DuctMassSource
{
    public required int Cell { get; init; }

    public required int SpeciesIndex { get; init; }

    /// <summary>kg/s; settable per step for injection timing.</summary>
    public double MassRate { get; set; }

    /// <summary>Vapour temperature at introduction, K.</summary>
    public required double Temperature { get; init; }
}

/// <summary>
/// External boundary at a duct end: supplies the ghost state each step from
/// the adjacent interior state. Set via <see cref="BoundaryKind.External"/>.
/// </summary>
public interface IEndBoundary
{
    EndGhost Ghost(
        in GasState interior, double rhoInterior, ReadOnlySpan<double> yInterior,
        bool isLeftEnd, IGasModel gas);
}

/// <summary>
/// Open end to a large reservoir/atmosphere (plan §2.7): stagnation
/// conditions feed subsonic inflow isentropically; outflow discharges against
/// the reservoir static pressure (= stagnation, the reservoir is at rest).
/// The inflow discharge coefficient models end geometry: ≈ 1.0 for a
/// bellmouth, ≈ 0.85 for a plain sharp pipe end (engineering defaults,
/// user-editable; the full acoustic end correction arrives with the TMM).
/// </summary>
public sealed class ReservoirBoundary : IEndBoundary
{
    public required double StagnationPressure { get; set; }

    public required double StagnationTemperature { get; set; }

    /// <summary>Reservoir composition; null for the perfect-gas model.</summary>
    public double[]? Composition { get; set; }

    public double InflowDischargeCoefficient { get; set; } = 1.0;

    public EndGhost Ghost(
        in GasState interior, double rhoInterior, ReadOnlySpan<double> yInterior,
        bool isLeftEnd, IGasModel gas)
    {
        // Characteristic-compatible subsonic boundary: the ghost state honours
        // the interior's outgoing Riemann invariant, so the boundary neither
        // creates nor destroys mass flow beyond what the reservoir physics
        // demands. Signed frame: ξ positive into the duct.
        var gamma = interior.Gamma;
        var g1 = gamma - 1.0;
        var r = interior.P / (rhoInterior * interior.T);
        var sign = isLeftEnd ? 1.0 : -1.0;

        var uIn = interior.U * sign;                     // interior velocity, into-duct positive
        var aInterior = interior.SoundSpeed;
        var rMinus = uIn - 2.0 * aInterior / g1;         // outgoing invariant (leaves through this end)

        // Try inflow: reservoir stagnation state (h0, s0) + interior R⁻.
        // With h0 = a0²/(γ−1)·... : u = √(2/(γ−1)·(a0² − a²)), and
        // f(a) = u(a) − 2a/(γ−1) − R⁻ = 0 solved for the boundary sound speed.
        var a0 = Math.Sqrt(gamma * r * StagnationTemperature);
        var canInflow = rMinus + 2.0 * a0 / g1 > 0.0;    // inflow solution exists
        if (canInflow)
        {
            var a = SolveInflowSoundSpeed(a0, g1, rMinus);
            if (a > 0)
            {
                var u = rMinus + 2.0 * a / g1;
                if (u >= 0)
                {
                    // Inflow confirmed: isentropic from reservoir conditions.
                    var t = a * a / (gamma * r);
                    var p = StagnationPressure * Math.Pow(t / StagnationTemperature, gamma / g1);
                    var rho = p / (r * t);
                    return new EndGhost(
                        new PrimitiveState(rho, sign * InflowDischargeCoefficient * u, p),
                        Composition);
                }
            }
        }

        // Outflow: impose reservoir pressure, keep interior entropy, and honour
        // the same outgoing invariant R⁻ = u − 2a/(γ−1) (into-duct frame): the
        // ghost velocity is u = R⁻ + 2a_out/(γ−1).
        var pOut = StagnationPressure;
        var rhoOut = rhoInterior * Math.Pow(pOut / interior.P, 1.0 / gamma);
        var aOut = Math.Sqrt(gamma * pOut / rhoOut);
        var uOutSigned = rMinus + 2.0 * aOut / g1;
        return new EndGhost(new PrimitiveState(rhoOut, sign * uOutSigned, pOut), null);
    }

    private static double SolveInflowSoundSpeed(double a0, double g1, double rMinus)
    {
        // f(a) = √(2/(γ−1)·(a0²−a²)) − 2a/(γ−1) − R⁻, monotone decreasing in a.
        var lo = 1e-6;
        var hi = a0;
        double F(double a)
        {
            var uSq = 2.0 / g1 * (a0 * a0 - a * a);
            return Math.Sqrt(Math.Max(0.0, uSq)) - 2.0 * a / g1 - rMinus;
        }

        if (F(hi) > 0)
        {
            return -1.0; // no subsonic inflow solution (interior demands outflow)
        }

        for (var i = 0; i < 60; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (F(mid) > 0)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        return 0.5 * (lo + hi);
    }
}
