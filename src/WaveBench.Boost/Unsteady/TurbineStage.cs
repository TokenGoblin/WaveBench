using WaveBench.Core.Components;
using WaveBench.Core.Solver;

namespace WaveBench.Boost.Unsteady;

/// <summary>Which of the plan's two turbine models is in use (§4.3).</summary>
public enum TurbineModelKind
{
    /// <summary>
    /// Map lookup at the instantaneous state, applied straight at the manifold
    /// outlet. Fast, the industry default, and adequate for steady matching.
    /// </summary>
    QuasiSteady,

    /// <summary>
    /// The volute meshed as a 1D duct terminated by the rotor. Recovers filling
    /// and emptying, and with it most of the hysteresis a real pulsating
    /// turbine shows, without a 3D solve.
    /// </summary>
    VoluteResolved,
}

/// <summary>Volute geometry, which is what separates the two models.</summary>
/// <param name="LengthM">Developed length of the volute passage from tongue to rotor.</param>
/// <param name="InletAreaM2">Cross-section where the manifold hands over.</param>
/// <param name="RotorAreaM2">Cross-section at the rotor face.</param>
/// <param name="Cells">Mesh for the resolved model.</param>
public sealed record VoluteGeometry(
    double LengthM,
    double InletAreaM2,
    double RotorAreaM2,
    int Cells = 12)
{
    /// <summary>Swept volume of the volute, m³ — one of two things the quasi-steady model throws away.</summary>
    public double VolumeM3 => 0.5 * (InletAreaM2 + RotorAreaM2) * LengthM;

    /// <summary>
    /// Below this the resolved model is not trustworthy, and the reason is
    /// worth stating: the handover junction and the rotor boundary end up a
    /// few millimetres apart, and the junction's linearised pressure solve
    /// then perturbs the rotor's expansion ratio directly instead of having a
    /// length of pipe to diffuse into. Measured on the pulsating rig, a 10 mm
    /// constant-area volute moved mean turbine power by up to 30% and moved it
    /// AGAIN with cell count, while a 150 mm one was mesh-independent to 0.2%.
    /// No real volute is 10 mm long; refusing the configuration is better than
    /// returning a number from it.
    /// </summary>
    public const double MinimumUsableLengthM = 0.030;

    public void Validate()
    {
        if (LengthM < MinimumUsableLengthM)
        {
            throw new ArgumentException(
                $"A resolved volute must be at least {MinimumUsableLengthM * 1000:F0} mm long; "
                + $"{LengthM * 1000:F1} mm was given. Below that the handover junction sits on top of the rotor "
                + "boundary and the answer depends on the mesh. Use the quasi-steady model instead — with a "
                + "volute this short it is what the resolved model is trying to approximate anyway.");
        }

        if (InletAreaM2 <= 0 || RotorAreaM2 <= 0)
        {
            throw new ArgumentException("Volute areas must be positive.");
        }

        if (Cells < 4)
        {
            throw new ArgumentException($"A resolved volute needs at least 4 cells; {Cells} were given.");
        }
    }

    public DuctGeometry ToDuct()
    {
        var inletDiameter = Math.Sqrt(4.0 * InletAreaM2 / Math.PI);
        var rotorDiameter = Math.Sqrt(4.0 * RotorAreaM2 / Math.PI);
        return DuctGeometry.Taper(LengthM, Cells, inletDiameter, rotorDiameter);
    }
}

/// <summary>One turbine entry: its rotor boundary and, when resolved, its volute duct.</summary>
public sealed class TurbineEntry
{
    internal TurbineEntry(string name, RotorNozzleBoundary rotor, DuctSolver? volute, Junction? handover)
    {
        Name = name;
        Rotor = rotor;
        Volute = volute;
        Handover = handover;
    }

    public string Name { get; }

    public RotorNozzleBoundary Rotor { get; }

    /// <summary>The resolved volute, or null under the quasi-steady model.</summary>
    public DuctSolver? Volute { get; }

    /// <summary>The junction handing the manifold over to the volute, or null.</summary>
    public Junction? Handover { get; }

    /// <summary>Work extracted so far, J.</summary>
    public double WorkJ { get; internal set; }

    /// <summary>Mass passed so far, kg.</summary>
    public double MassKg { get; internal set; }

    /// <summary>
    /// The state at the turbine ENTRY flange — where a gas stand puts its
    /// instrumentation, and therefore where the hysteresis loops of plan §4.3
    /// are measured.
    ///
    /// This is the whole distinction between the two models. Under the
    /// quasi-steady model the entry and the rotor face are the same place, so
    /// this traces the map's own curve and no loop can appear. With the volute
    /// resolved, the entry is upstream of a volume that fills and empties, so
    /// what goes in is out of phase with what the rotor takes out — and the
    /// trace opens into a loop.
    /// </summary>
    public (double ExpansionRatio, double MassFlowParameter, double MassFlowKgPerS) InletSample()
    {
        if (Volute is null)
        {
            var last = Rotor.Last;
            var mfp = last.TotalPressurePa > 0
                ? last.MassFlowKgPerS * Math.Sqrt(last.TotalTemperatureK) / last.TotalPressurePa
                : 0.0;
            return (last.ExpansionRatio, mfp, last.MassFlowKgPerS);
        }

        var state = Volute.GetState(0);
        var w = Volute.GetPrimitive(0);
        var area = Volute.Geometry.FaceArea[0];

        var mach = state.SoundSpeed > 0 ? Math.Abs(state.U) / state.SoundSpeed : 0.0;
        var factor = 1.0 + (0.5 * (state.Gamma - 1.0) * mach * mach);
        var pTotal = state.P * Math.Pow(factor, state.Gamma / (state.Gamma - 1.0));
        var tTotal = state.T * factor;

        var flow = w.Rho * state.U * area;
        return (
            pTotal / Rotor.OutletPressurePa,
            pTotal > 0 ? flow * Math.Sqrt(tTotal) / pTotal : 0.0,
            flow);
    }
}

/// <summary>
/// The turbine as it appears to the gas dynamics (plan §4.3): one or two
/// entries, each a rotor boundary optionally fed through a resolved volute,
/// with the shaft they all drive.
///
/// <b>Both models are always available and the difference is reported.</b> The
/// plan is explicit that quasi-steady "is not good enough, and the software
/// must say so": under engine pulsation, measured mass flow parameter and
/// efficiency describe hysteresis loops against pressure ratio rather than
/// single-valued curves, and the loops widen with pulse amplitude and
/// frequency. A quasi-steady model cannot produce a loop at all — it reads one
/// point off one curve — so where the two disagree, the volute volume is doing
/// something the user needs to know about.
/// </summary>
public sealed class TurbineStage
{
    private readonly List<TurbineEntry> _entries = [];

    private TurbineStage(TurbineModelKind kind, TurbineMap map, TurboShaft shaft)
    {
        Kind = kind;
        Map = map;
        Shaft = shaft;
    }

    public TurbineModelKind Kind { get; }

    public TurbineMap Map { get; }

    public TurboShaft Shaft { get; }

    public IReadOnlyList<TurbineEntry> Entries => _entries;

    /// <summary>Ducts this stage owns and the simulator must step. Empty under the quasi-steady model.</summary>
    public IEnumerable<DuctSolver> OwnedDucts => _entries.Select(e => e.Volute).OfType<DuctSolver>();

    /// <summary>Junctions this stage owns and the simulator must update.</summary>
    public IEnumerable<Junction> OwnedJunctions => _entries.Select(e => e.Handover).OfType<Junction>();

    /// <summary>Total work extracted across all entries, J.</summary>
    public double WorkJ => _entries.Sum(e => e.WorkJ);

    /// <summary>Total mass through the turbine, kg.</summary>
    public double MassKg => _entries.Sum(e => e.MassKg);

    /// <summary>Instantaneous shaft power from all entries, W.</summary>
    public double InstantaneousPowerW => _entries.Sum(e => e.Rotor.Last.PowerW);

    /// <summary>
    /// Build the stage onto one or two manifold outlets.
    /// </summary>
    /// <param name="kind">Quasi-steady or volute-resolved.</param>
    /// <param name="map">The turbine map. Both scrolls of a twin-scroll share it; admission splits the capacity.</param>
    /// <param name="shaft">The shaft this turbine drives.</param>
    /// <param name="outlets">
    /// The manifold ducts feeding the turbine, and which end of each feeds it.
    /// One entry for a single-scroll turbine, two for twin-scroll.
    /// </param>
    /// <param name="volute">Volute geometry. Required for the resolved model, and its volume is reported either way.</param>
    /// <param name="gas">The gas model, needed to mesh a volute.</param>
    /// <param name="outletPressurePa">p₄ downstream of the rotor.</param>
    public static TurbineStage Build(
        TurbineModelKind kind,
        TurbineMap map,
        TurboShaft shaft,
        IReadOnlyList<(DuctSolver Duct, bool LeftEnd, string Name)> outlets,
        VoluteGeometry volute,
        IGasModel gas,
        double outletPressurePa = 101_325.0)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(shaft);
        ArgumentNullException.ThrowIfNull(outlets);
        ArgumentNullException.ThrowIfNull(volute);
        ArgumentNullException.ThrowIfNull(gas);

        if (outlets.Count is < 1 or > 2)
        {
            throw new ArgumentException(
                $"A turbine stage takes one or two entries; {outlets.Count} were given.", nameof(outlets));
        }

        var stage = new TurbineStage(kind, map, shaft);

        foreach (var (duct, leftEnd, name) in outlets)
        {
            if (kind == TurbineModelKind.QuasiSteady)
            {
                // The rotor sits directly on the manifold outlet. The volute's
                // volume simply does not exist in this model, which is exactly
                // the approximation being tested.
                var area = leftEnd ? duct.Geometry.FaceArea[0] : duct.Geometry.FaceArea[^1];
                var rotor = new RotorNozzleBoundary(map, area, outletPressurePa) { ShaftRpm = shaft.Rpm };
                Attach(duct, leftEnd, rotor);
                stage._entries.Add(new TurbineEntry(name, rotor, null, null));
                continue;
            }

            volute.Validate();

            var voluteDuct = new DuctSolver(volute.ToDuct(), gas)
            {
                Cfl = duct.Cfl,
                Limiter = duct.Limiter,
            };

            var handover = new Junction(gas);
            handover.Connect(duct, leftEnd);
            handover.Connect(voluteDuct, true);

            var voluteRotor = new RotorNozzleBoundary(map, volute.RotorAreaM2, outletPressurePa)
            {
                ShaftRpm = shaft.Rpm,
            };

            voluteDuct.RightBoundary = BoundaryKind.External;
            voluteDuct.RightEnd = voluteRotor;

            stage._entries.Add(new TurbineEntry(name, voluteRotor, voluteDuct, handover));
        }

        // Twin-scroll starts at even admission and is redistributed every step
        // by the mixing-plane solve; single-entry stays at full admission.
        if (stage._entries.Count == 2)
        {
            foreach (var entry in stage._entries)
            {
                entry.Rotor.AdmissionFraction = 0.5;
            }
        }

        return stage;
    }

    private static void Attach(DuctSolver duct, bool leftEnd, RotorNozzleBoundary rotor)
    {
        if (leftEnd)
        {
            duct.LeftBoundary = BoundaryKind.External;
            duct.LeftEnd = rotor;
        }
        else
        {
            duct.RightBoundary = BoundaryKind.External;
            duct.RightEnd = rotor;
        }
    }

    /// <summary>
    /// Integrate the work and mass this stage has taken over one timestep, and
    /// advance the shaft against the compressor load.
    ///
    /// Called after the ducts have stepped, because the rotor states it reads
    /// were written during their boundary evaluation.
    /// </summary>
    public void Integrate(double dt, double compressorPowerW)
    {
        Accumulate(dt);

        Shaft.Step(dt, InstantaneousPowerW, compressorPowerW);
        foreach (var entry in _entries)
        {
            entry.Rotor.ShaftRpm = Shaft.Rpm;
        }

        Redistribute();
    }

    /// <summary>
    /// Integrate the work and mass but hold the shaft speed, which is how a gas
    /// stand runs a pulsating turbine test: the dynamometer holds the speed and
    /// the measurement is of the turbine alone, with no shaft dynamics mixed in.
    /// </summary>
    public void IntegrateAtFixedSpeed(double dt)
    {
        Accumulate(dt);
        Redistribute();
    }

    private void Accumulate(double dt)
    {
        foreach (var entry in _entries)
        {
            entry.WorkJ += entry.Rotor.Last.PowerW * dt;
            entry.MassKg += entry.Rotor.Last.MassFlowKgPerS * dt;
        }
    }

    private void Redistribute()
    {
        if (_entries.Count == 2)
        {
            TwinScrollTurbine.Redistribute(_entries[0].Rotor, _entries[1].Rotor);
        }
    }

    /// <summary>Reset the work and mass accumulators, e.g. at the start of a measured cycle.</summary>
    public void ResetAccumulators()
    {
        foreach (var entry in _entries)
        {
            entry.WorkJ = 0;
            entry.MassKg = 0;
        }
    }
}
