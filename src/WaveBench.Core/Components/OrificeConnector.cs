using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;

namespace WaveBench.Core.Components;

/// <summary>One side of an orifice connection.</summary>
public abstract class OrificeEndpoint
{
    /// <summary>Stagnation pressure, static pressure, stagnation temperature, gas constant, γ, composition.</summary>
    public abstract (double P0, double P, double T0, double R, double Gamma) Read();

    public abstract ReadOnlySpan<double> Composition { get; }

    public abstract double SpecificEnthalpy { get; }

    /// <summary>dm &gt; 0 = into this endpoint.</summary>
    public abstract void Apply(double dm, double dt, double hUpstream, ReadOnlySpan<double> yUpstream);
}

/// <summary>Constant ambient (atmosphere) endpoint.</summary>
public sealed class AmbientEndpoint(double pressure, double temperature, double gasConstant, double gamma, double[]? composition = null)
    : OrificeEndpoint
{
    private readonly double[] _composition = composition ?? [];

    public override (double, double, double, double, double) Read() =>
        (pressure, pressure, temperature, gasConstant, gamma);

    public override ReadOnlySpan<double> Composition => _composition;

    public override double SpecificEnthalpy => gamma * gasConstant / (gamma - 1.0) * temperature;

    public override void Apply(double dm, double dt, double hUpstream, ReadOnlySpan<double> yUpstream)
    {
        // Infinite reservoir: nothing to update.
    }
}

/// <summary>Plenum endpoint.</summary>
public sealed class PlenumEndpoint(PlenumVolume plenum, IGasModel gas) : OrificeEndpoint
{
    private readonly double[] _composition = new double[gas.SpeciesCount];

    public override (double, double, double, double, double) Read()
    {
        var s = plenum.LastState;
        var r = s.P / (plenum.Density * s.T);
        // The plenum gas is at rest: static = stagnation.
        return (s.P, s.P, s.T, r, s.Gamma);
    }

    public override ReadOnlySpan<double> Composition
    {
        get
        {
            for (var k = 0; k < _composition.Length; k++)
            {
                _composition[k] = plenum.MassFractions[k];
            }

            return _composition;
        }
    }

    public override double SpecificEnthalpy => plenum.SpecificEnthalpy;

    public override void Apply(double dm, double dt, double hUpstream, ReadOnlySpan<double> yUpstream) =>
        plenum.QueueFlow(dm, hUpstream, yUpstream);
}

/// <summary>
/// Duct-end endpoint: reads the adjacent interior cell; applies flow as a
/// flux override on the end face.
/// </summary>
public sealed class DuctEndpoint(DuctSolver duct, bool leftEnd) : OrificeEndpoint
{
    private readonly double[] _composition = new double[duct.Gas.SpeciesCount];

    public DuctSolver Duct => duct;

    public bool IsLeftEnd => leftEnd;

    private int AdjacentCell => leftEnd ? 0 : duct.CellCount - 1;

    public override (double, double, double, double, double) Read()
    {
        var s = duct.GetState(AdjacentCell);
        var rho = duct.GetPrimitive(AdjacentCell).Rho;
        var r = s.P / (rho * s.T);
        var mach = Math.Abs(s.U) / s.SoundSpeed;
        var p0 = s.P * Math.Pow(1.0 + 0.5 * (s.Gamma - 1.0) * mach * mach, s.Gamma / (s.Gamma - 1.0));
        return (p0, s.P, s.T * (1.0 + 0.5 * (s.Gamma - 1.0) * mach * mach), r, s.Gamma);
    }

    public override ReadOnlySpan<double> Composition
    {
        get
        {
            for (var k = 0; k < _composition.Length; k++)
            {
                _composition[k] = duct.GetMassFraction(k, AdjacentCell);
            }

            return _composition;
        }
    }

    public override double SpecificEnthalpy
    {
        get
        {
            var s = duct.GetState(AdjacentCell);
            var rho = duct.GetPrimitive(AdjacentCell).Rho;
            var r = s.P / (rho * s.T);
            var cp = s.Gamma * r / (s.Gamma - 1.0);
            return cp * s.T + 0.5 * s.U * s.U;
        }
    }

    public override void Apply(double dm, double dt, double hUpstream, ReadOnlySpan<double> yUpstream)
    {
        var area = leftEnd ? duct.Geometry.FaceArea[0] : duct.Geometry.FaceArea[^1];
        var mDot = dm / dt;

        // +x flux sense: into the duct at the left end is +, at the right end −.
        var fRho = (leftEnd ? mDot : -mDot) / area;
        var w = duct.GetPrimitive(AdjacentCell);
        var fMom = w.P + fRho * fRho / Math.Max(w.Rho, 1e-12);
        var fEner = fRho * (dm >= 0 ? hUpstream : SpecificEnthalpy);

        double[]? yIn = null;
        if (dm >= 0 && duct.Gas.SpeciesCount > 0)
        {
            yIn = yUpstream.ToArray();
        }

        if (leftEnd)
        {
            duct.LeftFluxOverride = (fRho, fMom, fEner);
            duct.LeftFluxComposition = yIn;
        }
        else
        {
            duct.RightFluxOverride = (fRho, fMom, fEner);
            duct.RightFluxComposition = yIn;
        }
    }
}

/// <summary>
/// Quasi-steady orifice between two endpoints (plan §2.6/§2.7): throttle,
/// plenum port, leak, transfer port. Separate discharge coefficients per
/// direction; effective area may be time-varying (throttle angle, valve lift).
/// </summary>
public sealed class OrificeConnector(OrificeEndpoint a, OrificeEndpoint b)
{
    public double EffectiveArea { get; set; }

    /// <summary>C_d for flow A → B.</summary>
    public double DischargeCoefficientAtoB { get; set; } = 0.8;

    /// <summary>C_d for flow B → A.</summary>
    public double DischargeCoefficientBtoA { get; set; } = 0.8;

    /// <summary>Mass flow of the last update, positive A → B, kg/s.</summary>
    public double MassFlow { get; private set; }

    public bool IsChoked { get; private set; }

    /// <summary>
    /// Compute the quasi-steady flow from the current endpoint states and
    /// apply it to both sides for the coming step of length dt.
    /// </summary>
    public void Update(double dt)
    {
        var (p0A, pA, t0A, rA, gA) = a.Read();
        var (p0B, pB, t0B, rB, gB) = b.Read();

        double mDot;
        if (p0A >= p0B)
        {
            mDot = CompressibleOrifice.MassFlow(DischargeCoefficientAtoB, EffectiveArea, p0A, t0A, pB, gA, rA);
            IsChoked = CompressibleOrifice.IsChoked(pB / p0A, gA);
        }
        else
        {
            mDot = -CompressibleOrifice.MassFlow(DischargeCoefficientBtoA, EffectiveArea, p0B, t0B, pA, gB, rB);
            IsChoked = CompressibleOrifice.IsChoked(pA / p0B, gB);
        }

        MassFlow = mDot;
        var dm = mDot * dt;

        if (mDot >= 0)
        {
            var h = a.SpecificEnthalpy;
            var y = a.Composition;
            a.Apply(-dm, dt, h, y);
            b.Apply(dm, dt, h, y);
        }
        else
        {
            var h = b.SpecificEnthalpy;
            var y = b.Composition;
            a.Apply(-dm, dt, h, y);
            b.Apply(dm, dt, h, y);
        }
    }
}

/// <summary>
/// Butterfly throttle (plan §2.7): effective area from plate angle by the
/// standard geometric approximation A_eff/A = 1 − cos θ / cos θ₀ (θ₀ the
/// closed-plate rest angle), clamped to a leakage floor. Replaceable by a
/// measured C_d(angle) map via <see cref="AreaOverride"/>.
/// </summary>
public sealed class ThrottleValve(double boreDiameter, double closedAngleDegrees = 7.0)
{
    public double BoreArea { get; } = Math.PI / 4.0 * boreDiameter * boreDiameter;

    public double LeakageFraction { get; set; } = 0.005;

    /// <summary>Optional measured effective-area map (angle° → m²).</summary>
    public Func<double, double>? AreaOverride { get; set; }

    public double EffectiveArea(double angleDegrees)
    {
        if (AreaOverride is not null)
        {
            return AreaOverride(angleDegrees);
        }

        var theta = Math.Clamp(angleDegrees, closedAngleDegrees, 90.0);
        var open = 1.0 - Math.Cos(theta * Math.PI / 180.0) / Math.Cos(closedAngleDegrees * Math.PI / 180.0);
        return BoreArea * Math.Max(LeakageFraction, open);
    }
}
