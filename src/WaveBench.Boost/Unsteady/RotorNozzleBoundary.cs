using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;

namespace WaveBench.Boost.Unsteady;

/// <summary>The instantaneous state of the rotor boundary, sampled every solver step.</summary>
/// <param name="MassFlowKgPerS">Flow into the rotor. Zero when the pulse has collapsed and the end blocks.</param>
/// <param name="PowerW">Shaft power at this instant.</param>
/// <param name="TotalPressurePa">p₀₃ at the rotor face.</param>
/// <param name="TotalTemperatureK">T₀₃.</param>
/// <param name="ExpansionRatio">p₀₃/p₄.</param>
/// <param name="Efficiency">η_ts read from the map at this instant.</param>
/// <param name="BladeSpeedRatio">U_tip/C_is, or NaN when the map states no rotor diameter.</param>
/// <param name="Region">Whether the map reading was measured or extrapolated.</param>
public readonly record struct RotorState(
    double MassFlowKgPerS,
    double PowerW,
    double TotalPressurePa,
    double TotalTemperatureK,
    double ExpansionRatio,
    double Efficiency,
    double BladeSpeedRatio,
    MapRegion Region);

/// <summary>
/// The turbine rotor as a duct end boundary (plan §4.3): a swallowing capacity
/// plus work extraction.
///
/// This is the component that makes forced induction unsteady rather than a
/// matching exercise. The rotor does not impose a pressure and it does not
/// impose a flow — it imposes a <i>relationship</i> between them, and the duct's
/// outgoing characteristic imposes another. Where the two meet is the boundary
/// state, and solving for it every step is what lets a blowdown pulse arrive,
/// do work, and reflect.
///
/// Placing this boundary directly on the manifold outlet gives the quasi-steady
/// model. Placing it on the far end of a volute duct gives the volute-resolved
/// model — the same boundary, a different topology — and the difference between
/// the two is the filling and emptying of the volute, which is where the
/// measured hysteresis loops come from.
/// </summary>
public sealed class RotorNozzleBoundary : IEndBoundary
{
    private readonly double _endAreaM2;

    /// <summary>Handles the inflow half of the boundary; see <see cref="OutletTemperatureK"/>.</summary>
    private readonly ReservoirBoundary _backflow = new()
    {
        StagnationPressure = 101_325.0,
        StagnationTemperature = 800.0,
    };

    public RotorNozzleBoundary(TurbineMap map, double endAreaM2, double outletPressurePa = 101_325.0)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(endAreaM2);

        Map = map;
        _endAreaM2 = endAreaM2;
        OutletPressurePa = outletPressurePa;
    }

    public TurbineMap Map { get; }

    /// <summary>Actual shaft speed, rpm. Driven by <see cref="TurboShaft"/> during a transient.</summary>
    public double ShaftRpm { get; set; } = 100_000.0;

    /// <summary>p₄, downstream of the rotor.</summary>
    public double OutletPressurePa { get; set; }

    /// <summary>
    /// T₄, the temperature of whatever would be drawn back in.
    ///
    /// An exhaust outlet does not only blow. Between pulses the manifold falls
    /// below the downstream pressure and gas comes back — and a boundary that
    /// forbids it is a check valve, not an outlet. That is not a subtle error:
    /// with backflow suppressed the manifold cannot relieve itself, mean
    /// turbine-inlet pressure climbs, the engine stops breathing, and a primary
    /// eventually goes to NaN at the junction. The symptom got WORSE under mesh
    /// refinement, which is the signature of an ill-posed boundary rather than
    /// an under-resolved one.
    /// </summary>
    public double OutletTemperatureK { get; set; } = 800.0;

    /// <summary>
    /// Fraction of the rotor annulus this entry is feeding (plan §4.3 partial
    /// admission). 1 for a single-entry turbine; for twin-scroll it is the
    /// share each scroll is admitting through at this instant, and it is
    /// emphatically not 0.5 all the time — see <see cref="TwinScrollTurbine"/>.
    /// </summary>
    public double AdmissionFraction { get; set; } = 1.0;

    /// <summary>
    /// Scales the map's swallowing capacity. A wastegate diverting flow leaves
    /// the rotor passing less; a VGT closing its vanes passes less at the same
    /// pressure ratio. Both act here rather than by editing the map.
    /// </summary>
    public double CapacityScale { get; set; } = 1.0;

    /// <summary>
    /// Scales the map's efficiency. Partial admission at a twin-entry rotor
    /// costs efficiency without changing how much the rotor swallows, so it
    /// acts here and not on <see cref="CapacityScale"/>.
    /// </summary>
    public double EfficiencyScale { get; set; } = 1.0;

    /// <summary>The most recent solved state. Written every time the solver asks for a ghost.</summary>
    public RotorState Last { get; private set; }

    /// <summary>Set false to run the end as a plain open end — used to isolate the rotor's own effect.</summary>
    public bool Enabled { get; set; } = true;

    public EndGhost Ghost(
        in GasState interior, double rhoInterior, ReadOnlySpan<double> yInterior,
        bool isLeftEnd, IGasModel gas)
    {
        var gamma = interior.Gamma;
        var g1 = gamma - 1.0;
        var sign = isLeftEnd ? 1.0 : -1.0;
        var uIn = interior.U * sign;                  // into-duct positive
        var aInterior = interior.SoundSpeed;

        // Boundary sound speed at which the outflow is exactly zero, and the
        // pressure that goes with it along the interior isentrope. Above this
        // pressure the rotor would be pushing gas back up the duct; the search
        // never goes there, and clamping to it is the right answer for a rotor
        // that has stopped swallowing — the pulse reflects off a blocked end.
        // Outflow speed is −u_in + 2(a_i − a_b)/(γ−1), so it reaches zero at
        // a_b = a_i − u_in·(γ−1)/2 — MINUS, because u_in is positive INTO the
        // duct and an outflowing end has it negative. Getting that sign wrong
        // puts the blocking pressure below the back-pressure whenever gas is
        // leaving, which reads as permanent backflow and shows up as an engine
        // that will not breathe.
        var aBlocked = aInterior - (uIn * g1 / 2.0);
        var pBlocked = aBlocked > 0
            ? interior.P * Math.Pow(aBlocked / aInterior, 2.0 * gamma / g1)
            : interior.P * 1e-6;

        // The face cannot flow faster than sonic, and forgetting that is a
        // spectacular failure rather than a small error. Left unclamped, a
        // 5-bar pulse expanding to a 1-bar back-pressure gives the face a
        // Mach number of several, and the TOTAL pressure computed from it —
        // which is what the map is read against — comes out at five bar with an
        // expansion ratio of four. The rotor then reports a swallowing capacity
        // it could never have, the boundary state stops being consistent with
        // the interior, and a primary goes to NaN a few hundred degrees later.
        //
        // The choked face pressure follows from the outgoing invariant with
        // u = a:  a_crit = [(γ−1)·(−u_in) + 2·a_i]/(γ+1), and p from the
        // interior isentrope.
        var aCritical = ((g1 * -uIn) + (2.0 * aInterior)) / (gamma + 1.0);
        var pChoked = aCritical > 0
            ? interior.P * Math.Pow(aCritical / aInterior, 2.0 * gamma / g1)
            : 0.0;

        // Backflow. When the pressure that would stop the flow has already
        // fallen below what is downstream, gas is coming IN, and the state it
        // brings is the downstream reservoir's rather than the interior's.
        // Delegating to the reservoir boundary is both simpler and more correct
        // than extending the outflow isentrope backwards through a
        // discontinuity that is not there.
        if (pBlocked <= OutletPressurePa)
        {
            _backflow.StagnationPressure = OutletPressurePa;
            _backflow.StagnationTemperature = OutletTemperatureK;
            var inflow = _backflow.Ghost(interior, rhoInterior, yInterior, isLeftEnd, gas);

            // The rotor produces nothing while it is being back-fed, but the
            // pressure and the (negative) flow are still real and the delivery
            // metrics need them: recording a zero here would drag the cycle-mean
            // turbine inlet pressure down toward nothing and make every pulse
            // look like it arrived above the mean.
            Last = new RotorState(
                inflow.State.Rho * inflow.State.U * _endAreaM2 * sign * -1.0,
                0.0,
                inflow.State.P,
                inflow.State.P / (inflow.State.Rho * (interior.P / (rhoInterior * interior.T))),
                1.0, 0.0, double.NaN, MapRegion.Measured);

            return inflow;
        }

        var pLow = Math.Min(Math.Max(OutletPressurePa, pChoked), pBlocked);
        var cp = gas.Cp(rhoInterior, interior.P, yInterior);
        var rGas = interior.P / (rhoInterior * interior.T);

        // The interior state, copied out of the `in` parameter so the local
        // functions below can close over it.
        var upstream = new Upstream(interior.P, interior.SoundSpeed, rhoInterior, rGas, gamma, uIn);

        if (!Enabled)
        {
            var open = Face(upstream, pLow);
            Last = default;
            return new EndGhost(new PrimitiveState(open.Rho, -sign * open.Speed, pLow), null);
        }

        // f(p) = what the duct will deliver at this boundary pressure minus what
        // the rotor will swallow at it. The duct term falls with pressure and
        // the rotor term rises, so f crosses zero exactly once and bisection is
        // safe without a derivative.
        double Imbalance(double p)
        {
            var face = Face(upstream, p);
            return (face.Speed * face.Rho * _endAreaM2) - Capacity(face, cp).Flow;
        }

        double pBoundary;

        if (Imbalance(pLow) <= 0)
        {
            // The rotor swallows more than the duct can deliver even with no
            // back-pressure at all: it is not the restriction here, and the end
            // behaves as though open to p₄.
            pBoundary = pLow;
        }
        else if (Imbalance(pBlocked) >= 0)
        {
            pBoundary = pBlocked;
        }
        else
        {
            var lo = pLow;
            var hi = pBlocked;
            for (var i = 0; i < 60 && hi - lo > 1e-9 * hi; i++)
            {
                var mid = 0.5 * (lo + hi);
                if (Imbalance(mid) > 0)
                {
                    lo = mid;
                }
                else
                {
                    hi = mid;
                }
            }

            pBoundary = 0.5 * (lo + hi);
        }

        var solved = Face(upstream, pBoundary);
        var (flow, state) = Capacity(solved, cp);

        // Below the blocking pressure the duct is the binding constraint on how
        // much actually crosses the face, so report what crossed, not what the
        // rotor would have taken.
        var crossed = Math.Min(flow, solved.Speed * solved.Rho * _endAreaM2);
        var scale = flow > 0 ? crossed / flow : 0.0;
        Last = state with { MassFlowKgPerS = crossed, PowerW = state.PowerW * scale };

        return new EndGhost(new PrimitiveState(solved.Rho, -sign * solved.Speed, pBoundary), null);
    }

    /// <summary>The interior state at the last cell, in the into-duct frame.</summary>
    private readonly record struct Upstream(
        double P, double SoundSpeed, double Rho, double RGas, double Gamma, double UIn);

    /// <summary>The boundary face state implied by holding it at a given static pressure.</summary>
    private readonly record struct FaceState(
        double Rho, double Speed, double PStatic, double TStatic, double PTotal, double TTotal);

    private static FaceState Face(in Upstream up, double p)
    {
        var g1 = up.Gamma - 1.0;
        var rho = up.Rho * Math.Pow(p / up.P, 1.0 / up.Gamma);
        var a = Math.Sqrt(up.Gamma * p / rho);

        // Outflow magnitude from the outgoing invariant, into-duct frame:
        //   u_b = u_i + 2(a_b − a_i)/(γ−1), and the outflow speed is −u_b.
        var speed = Math.Max(0.0, -up.UIn + (2.0 * (up.SoundSpeed - a) / g1));

        var tStatic = p / (rho * up.RGas);
        var mach = a > 0 ? speed / a : 0.0;
        var factor = 1.0 + (0.5 * g1 * mach * mach);

        return new FaceState(rho, speed, p, tStatic, p * Math.Pow(factor, up.Gamma / g1), tStatic * factor);
    }

    /// <summary>
    /// What the rotor swallows and produces with this state at its face.
    ///
    /// Maps are quoted against TOTAL inlet conditions. At 150 m/s in 1000 K
    /// exhaust that is 2–3% of pressure — small, but it goes straight into the
    /// expansion ratio and therefore into the power.
    /// </summary>
    private (double Flow, RotorState State) Capacity(in FaceState face, double cp)
    {
        var expansionRatio = Math.Max(1.0, face.PTotal / OutletPressurePa);
        var correctedSpeed = Corrected.Speed(ShaftRpm, face.TTotal, Map.Reference);
        var (correctedFlow, mapEfficiency, region) = TurbineModel.ReadMap(Map, expansionRatio, correctedSpeed);
        var efficiency = Math.Clamp(mapEfficiency * EfficiencyScale, 0.0, 1.0);

        var flow = AdmissionFraction * CapacityScale * Corrected.ActualFlow(
            correctedFlow, face.TTotal, face.PTotal / 1000.0, Map.Reference);

        // γ for the ideal drop comes from the gas model through c_p and the
        // face state rather than a hard-coded 1.33: the steady matcher in
        // TurbineModel has no gas model and uses constants, but here the real
        // mixture is to hand and its γ falls as the products get hotter.
        var gamma = cp / (cp - (face.PStatic / (face.Rho * face.TStatic)));
        var idealDrop = 1.0 - Math.Pow(expansionRatio, -(gamma - 1.0) / gamma);
        var power = flow * cp * face.TTotal * efficiency * idealDrop;

        return (flow, new RotorState(
            flow, power, face.PTotal, face.TTotal, expansionRatio, efficiency,
            BladeSpeedRatio(cp, face.TTotal, idealDrop), region));
    }

    /// <summary>BSR = U_tip/C_is with C_is = √(2·c_p·T₀₃·[1 − ER^(−(γ−1)/γ)]).</summary>
    private double BladeSpeedRatio(double cp, double totalTemperatureK, double idealDrop)
    {
        if (Map.RotorDiameterM is not { } diameter || diameter <= 0)
        {
            return double.NaN;
        }

        var cIsentropic = Math.Sqrt(Math.Max(0.0, 2.0 * cp * totalTemperatureK * idealDrop));
        return cIsentropic > 0 ? Math.PI * diameter * ShaftRpm / 60.0 / cIsentropic : double.NaN;
    }
}
