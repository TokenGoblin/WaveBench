using System.Globalization;
using WaveBench.Boost;
using WaveBench.Boost.Control;
using WaveBench.Boost.Engine;
using WaveBench.Core.EngineModel;
using WaveBench.Core.Thermo.Fuels;
using WaveBench.Model;
using WaveBench.ViewModels.Plotting;

namespace WaveBench.ViewModels;

/// <summary>
/// Whether a figure is showing the instant estimate or the solved answer.
/// Shown on every figure, because an estimate presented as a solve is worse
/// than no estimate — the same rule the Sound workspace follows.
/// </summary>
public enum BoostFidelity
{
    /// <summary>
    /// From the maps, the geometry and one assumed volumetric efficiency. Runs
    /// in milliseconds, so it can follow a slider.
    /// </summary>
    Instant,

    /// <summary>Volumetric efficiency and exhaust temperature taken from a converged sweep.</summary>
    Solved,
}

/// <summary>
/// The engine's requirement and what the turbo actually delivers at one speed.
/// </summary>
/// <param name="EngineRpm">Engine speed.</param>
/// <param name="Demand">Flow and target pressure ratio the engine asks for.</param>
/// <param name="Match">
/// Where the engine actually runs: the shaft balance with the wastegate
/// holding the target where the turbo would otherwise exceed it. This is the
/// point that belongs on the compressor map, because it is the one the
/// compressor is at.
/// </param>
/// <param name="WideOpen">
/// The same balance with the gate shut — what the turbo WOULD make. Only the
/// Control tab wants this: it is the difference between the two that says how
/// much the gate has to bleed, and drawing it on the map would be drawing an
/// engine that never runs.
/// </param>
/// <param name="Inlet">Compressor inlet condition, sub-atmospheric behind a restrictor.</param>
/// <param name="ManifoldTemperatureK">Post-cooler charge temperature.</param>
/// <param name="RestrictorChoked">True where the restrictor, not the turbo, is setting the flow.</param>
public sealed record BoostOperatingPoint(
    double EngineRpm,
    BoostDemandPoint Demand,
    MatchPoint Match,
    MatchPoint WideOpen,
    RestrictorState Inlet,
    double ManifoldTemperatureK,
    bool RestrictorChoked)
{
    /// <summary>Delivered manifold gauge pressure, kPa — what a boost gauge would read.</summary>
    public double DeliveredBoostKPa =>
        (Match.Compressor.PressureRatio * Inlet.OutletTotalPressureKPa) - CoolerDropKPa - AmbientKPa;

    /// <summary>What the turbo would deliver with the gate shut, kPa gauge.</summary>
    public double WideOpenBoostKPa =>
        (WideOpen.Compressor.PressureRatio * Inlet.OutletTotalPressureKPa) - CoolerDropKPa - AmbientKPa;

    /// <summary>Ambient the point was solved against, kPa absolute.</summary>
    public required double AmbientKPa { get; init; }

    /// <summary>Charge-cooler core drop, kPa: boost measured before the core is boost the engine never sees.</summary>
    public required double CoolerDropKPa { get; init; }

    /// <summary>Boost the engine asked for, kPa gauge.</summary>
    public required double TargetBoostKPa { get; init; }

    /// <summary>How much boost the wastegate has to throw away here. Negative is a shortfall it cannot fix.</summary>
    public double SurplusKPa => WideOpenBoostKPa - TargetBoostKPa;

    /// <summary>True where the gate is doing something — the turbo can exceed the target at this speed.</summary>
    public bool Gated => SurplusKPa > 0.5;
}

/// <summary>
/// The Boost workspace (plan Phase 21, §8.4): compressor map overlay with the
/// operating line and its margins, the turbine A/R sweep, boost control,
/// charge cooling with heat soak, and the transient view — plus auto-match
/// ranking the library against this engine.
///
/// <b>The instant path is the point, as it is in Sound.</b> Every figure here
/// except the auto-match comes from the maps and a quasi-steady flow balance,
/// which is milliseconds — so changing a boost target or an A/R redraws the
/// operating line immediately, and the nonlinear re-solve is queued behind it.
/// <see cref="Fidelity"/> says which answer a figure is showing.
///
/// Contains no UI-framework types.
/// </summary>
public sealed class BoostWorkspace : IFieldEditingSurface
{
    /// <summary>Ratio of specific heats for air, matching <see cref="CompressorModel"/>'s own constant.</summary>
    private const double Gamma = 1.4;

    private const double GasConstant = 287.05;

    private readonly ProjectSession _session;
    private readonly Dictionary<string, string> _rejections = new(StringComparer.OrdinalIgnoreCase);

    public BoostWorkspace(ProjectSession session, UserPreferences? preferences = null, RunResult? run = null)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Preferences = preferences ?? new UserPreferences();
        Run = run;
        Ambient = DocumentAmbient();

        ToRpm = PracticalTopRpm();
        FromRpm = Math.Clamp(Math.Round(ToRpm / 4.0 / 250.0) * 250.0, 1000.0, 3000.0);
        TransientRpm = Math.Clamp(Math.Round(ToRpm * 0.4 / 250.0) * 250.0, 1500.0, 4000.0);
    }

    public UserPreferences Preferences { get; }

    public EngineModelDocument Document => _session.Document;

    public ForcedInductionSpec Spec => Document.ForcedInduction;

    /// <summary>The solved sweep, when one exists. Its presence is what makes the figures <see cref="BoostFidelity.Solved"/>.</summary>
    public RunResult? Run { get; set; }

    public BoostFidelity Fidelity => Run is { Points.Count: > 0 } ? BoostFidelity.Solved : BoostFidelity.Instant;

    public BoostTab SelectedTab { get; set; } = BoostTab.Compressor;

    /// <summary>The most recent rejection per field, for the view to show inline.</summary>
    public IReadOnlyDictionary<string, string> Rejections => _rejections;

    private FieldEditor Editor => new(_session, Preferences);

    // ---- Fields -----------------------------------------------------------

    /// <summary>
    /// Fields to draw on a tab, filtered by mode — the same contract
    /// <see cref="DesignWorkspace.Fields"/> honours, and drawn by the same
    /// renderer.
    /// </summary>
    public IReadOnlyList<FieldView> Fields(BoostTab tab) =>
        BoostCatalogue.For(tab)
            .Where(f => Preferences.Mode == UiMode.Advanced || f.Simple)
            .Where(Relevant)
            .Select(Editor.View)
            .ToList();

    /// <summary>
    /// Sub-fields of a switched-off feature are hidden rather than greyed:
    /// a restrictor throat diameter on a car with no restrictor is not a
    /// setting, it is noise. The parent toggle is always shown, so nothing
    /// becomes unreachable.
    /// </summary>
    private bool Relevant(BoostField field) => field.Path switch
    {
        "ForcedInduction.RestrictorThroatMm"
            or "ForcedInduction.RestrictorDischargeCoefficient"
            or "ForcedInduction.RestrictorDiffuserRecovery" => Spec.RestrictorFitted,

        "ForcedInduction.CoolerEffectiveness"
            or "ForcedInduction.CoolerPressureDropKPa"
            or "ForcedInduction.CoolantTemperatureK" => HasCooler,

        _ => true,
    };

    public bool HasCooler =>
        !string.Equals(Spec.ChargeCooler, ChargeCoolerKinds.None, StringComparison.OrdinalIgnoreCase);

    /// <summary>Apply a user edit, in display units, through the session.</summary>
    public EditOutcome Edit(string path, string text)
    {
        var field = BoostCatalogue.Find(path);
        var outcome = field is null
            ? EditOutcome.Reject($"'{path}' is not an editable boost field.")
            : Editor.Apply(field, text);

        if (outcome.Accepted)
        {
            _rejections.Remove(path);
        }
        else if (outcome.Reason is { } reason)
        {
            _rejections[path] = reason;
        }

        return outcome;
    }

    /// <summary>Model issues the document's validator raises against this block.</summary>
    public IReadOnlyList<ModelIssue> Issues() =>
        Document.Validate()
            .Where(i => i.Path.StartsWith("forcedInduction", StringComparison.OrdinalIgnoreCase))
            .ToList();

    // ---- Hardware ---------------------------------------------------------

    /// <summary>
    /// The chosen turbo, re-housed to the A/R the user is considering.
    /// Null when nothing is chosen, which every figure reports rather than
    /// drawing an empty axis.
    /// </summary>
    public Turbocharger? Turbo => TurboAt(Spec.TurbineAreaRatio);

    /// <summary>The chosen unit with a given turbine housing on it.</summary>
    public Turbocharger? TurboAt(double areaRatio)
    {
        if (TurboLibrary.TurboFor(Spec.TurboName) is not { } turbo)
        {
            return null;
        }

        return turbo with { Turbine = TurboLibrary.Rehoused(turbo.Turbine, areaRatio) };
    }

    public TurboEntry? Entry => TurboLibrary.Find(Spec.TurboName);

    public IntakeRestrictor? Restrictor => Spec.RestrictorFitted
        ? new IntakeRestrictor
        {
            ThroatDiameterM = Spec.RestrictorThroatMm / 1000.0,
            DischargeCoefficient = Spec.RestrictorDischargeCoefficient,
            DiffuserRecovery = Spec.RestrictorDiffuserRecovery,
        }
        : null;

    // ---- Ambient ----------------------------------------------------------

    /// <summary>
    /// The condition the figures are drawn at.
    ///
    /// <b>A VIEW selection, never an edit.</b> Plan §4.7 asks for an altitude
    /// and hot-day toggle "because that is where matches fail" — the point is
    /// to see the same design at a different condition, not to change the
    /// design's own ambient, which lives in
    /// <c>Document.Ambient</c> and is edited in Design → Fuel &amp;
    /// Combustion. Switching this cannot touch the document.
    /// </summary>
    public AmbientCondition Ambient { get; set; }

    /// <summary>The document's own condition, plus the standard alternatives to check a match against.</summary>
    public IReadOnlyList<AmbientCondition> AmbientChoices =>
        [DocumentAmbient(), .. AmbientCondition.Standard];

    public bool ShowingDocumentAmbient =>
        Math.Abs(Ambient.PressurePa - DocumentAmbient().PressurePa) < 1e-6
        && Math.Abs(Ambient.TemperatureK - DocumentAmbient().TemperatureK) < 1e-9;

    private AmbientCondition DocumentAmbient() => new(
        Document.Ambient.PressureKPa * 1000.0,
        Document.Ambient.TemperatureK,
        $"This model ({Document.Ambient.PressureKPa:F1} kPa, {Document.Ambient.TemperatureK - 273.15:F0} °C)");

    // ---- Speed range ------------------------------------------------------

    /// <summary>
    /// The speed range the figures are drawn over.
    ///
    /// Defaulted from the engine's own geometry rather than from a fixed
    /// number, because a fixed one is wrong for everything: 2000–8000 rpm runs
    /// a long-stroke road four past anything it would survive and stops a
    /// 42 mm-stroke race engine 6000 rpm short of its power peak — and an
    /// operating line that stops short of the top end hides exactly the choke
    /// margin the screen exists to show. Mean piston speed is the limit that
    /// actually scales: 20 m/s is where sustained road practice stops
    /// (the same figure Design → Engine quotes). The user can drag it anywhere.
    /// </summary>
    public double FromRpm { get; set; }

    public double ToRpm { get; set; }

    public int Steps { get; set; } = 13;

    /// <summary>The speed 20 m/s mean piston speed falls at, rounded to something a slider would land on.</summary>
    private double PracticalTopRpm()
    {
        var strokeM = Document.Engine.StrokeMm / 1000.0;
        if (strokeM <= 0)
        {
            return 8000.0;
        }

        // Mean piston speed = 2·S·N/60, so N at 20 m/s is 600/S.
        var top = 600.0 / strokeM;
        return Math.Clamp(Math.Round(top / 250.0) * 250.0, 4000.0, 16_000.0);
    }

    /// <summary>
    /// Volumetric efficiency the instant estimate assumes, when no solved
    /// sweep is available to supply the real one. An ASSUMPTION, stated on
    /// every figure that rests on it.
    /// </summary>
    public double AssumedVolumetricEfficiency { get; set; } = 0.95;

    public IReadOnlyList<double> Speeds()
    {
        var n = Math.Max(2, Steps);
        return Enumerable.Range(0, n)
            .Select(i => FromRpm + ((ToRpm - FromRpm) * i / (n - 1.0)))
            .ToList();
    }

    // ---- The quasi-steady flow balance ------------------------------------

    /// <summary>
    /// The engine's air demand and where the shaft settles, at every speed.
    ///
    /// <b>Quasi-steady, and closed by iteration rather than assumed.</b> The
    /// engine's air flow depends on the manifold density, which depends on the
    /// boost, which depends on the compressor's efficiency and speed, which
    /// depend on the flow. Three passes close that loop to well inside the
    /// uncertainty on the volumetric efficiency it starts from. What it does
    /// NOT contain is any wave action: manifold filling, pulse energy at the
    /// turbine and the scavenging window are the gas-dynamic solve's business
    /// (Phase 13), and this is the screen that runs while you drag a slider.
    /// </summary>
    public IReadOnlyList<BoostOperatingPoint> OperatingLine() => OperatingLineAt(Spec.TurbineAreaRatio);

    public IReadOnlyList<BoostOperatingPoint> OperatingLineAt(double areaRatio)
    {
        if (TurboAt(areaRatio) is not { } turbo)
        {
            return [];
        }

        return Speeds().Select(rpm => Solve(turbo, rpm)).ToList();
    }

    private BoostOperatingPoint Solve(Turbocharger turbo, double rpm)
    {
        var ambientPa = Ambient.PressurePa;
        var ambientK = Ambient.TemperatureK;
        var ambientKPa = ambientPa / 1000.0;
        var targetManifoldKPa = ambientKPa + Spec.TargetBoostKPa;
        var restrictor = Restrictor;
        var turbineOutKPa = Spec.ExhaustBackPressureKPa;

        var volumetricEfficiency = VolumetricEfficiencyAt(rpm);
        var turbineInletK = TurbineInletTemperatureAt(rpm);
        var displacement = Displacement();

        // Seeded at the target, then walked toward what the turbo can
        // actually hold. Starting from ambient would take more passes to
        // converge and starting from the target is the honest first guess:
        // it is what the user asked for.
        var manifoldKPa = targetManifoldKPa;
        var efficiency = 0.72;

        double airFlow = 0.0;
        var inlet = new RestrictorState(0, ambientKPa, ambientK, 1.0, false, double.PositiveInfinity);
        var manifoldK = ambientK;
        var choked = false;
        MatchPoint? wideOpen = null;
        MatchPoint? match = null;

        for (var pass = 0; pass < 3; pass++)
        {
            // Inlet: ambient, or whatever the restrictor leaves behind. The
            // restrictor is solved at the CURRENT flow estimate, which is why
            // it has to be inside the loop.
            inlet = restrictor is null
                ? new RestrictorState(airFlow, ambientKPa, ambientK, 1.0, false, double.PositiveInfinity)
                : restrictor.Solve(airFlow, ambientPa, ambientK);

            // The compressor is asked for the manifold target PLUS the cooler
            // core's drop: boost measured before the core is boost the engine
            // never sees.
            var pressureRatio = (manifoldKPa + Spec.CoolerPressureDropKPa) / inlet.OutletTotalPressureKPa;

            // Charge temperature: aerodynamic rise, then the cooler.
            var outletK = inlet.OutletTotalTemperatureK
                          * (1.0 + ((Math.Pow(Math.Max(1.0, pressureRatio), (Gamma - 1.0) / Gamma) - 1.0) / efficiency));
            manifoldK = HasCooler
                ? outletK - (Spec.CoolerEffectiveness * (outletK - Spec.CoolantTemperatureK))
                : outletK;

            // Four-stroke: one induction per two revolutions.
            var density = manifoldKPa * 1000.0 / (GasConstant * manifoldK);
            airFlow = volumetricEfficiency * displacement * (rpm / 120.0) * density;

            // Choked is a statement about THIS pass's flow, set after it is
            // known. Reading it off the inlet state — which was solved at the
            // previous pass's flow — reported a point as choked while its own
            // flow sat below the ceiling, which is the one place a reader would
            // look to decide whether the engine or the throat is in charge.
            if (restrictor is { } r)
            {
                var ceiling = r.ChokedFlow(ambientPa, ambientK);
                airFlow = Math.Min(airFlow, ceiling);
                choked = airFlow >= ceiling - (1e-9 * Math.Max(1.0, ceiling));
            }

            // Where the shaft settles against this flow with the gate SHUT.
            // The turbine's expansion ratio is not free — the engine's exhaust
            // has to go through it — so this is what decides whether the
            // target is reachable at all.
            wideOpen = ShaftBalance.Match(
                turbo, airFlow, ExhaustFlow(airFlow), inlet.OutletTotalTemperatureK,
                inlet.OutletTotalPressureKPa, turbineInletK, turbineOutKPa);

            // ...and then the gate. Where the turbo can exceed the target it
            // is held to it, and the engine runs at the CONTROLLED point.
            match = HoldToTarget(
                turbo, wideOpen, airFlow, inlet, targetManifoldKPa, Spec.CoolerPressureDropKPa,
                turbineInletK, turbineOutKPa);

            efficiency = Math.Clamp(match.Compressor.Efficiency, 0.3, 0.85);

            // What the engine actually sees next pass: the gate can throw
            // surplus away but nothing can invent a shortfall.
            var delivered = (match.Compressor.PressureRatio * inlet.OutletTotalPressureKPa)
                            - Spec.CoolerPressureDropKPa;
            manifoldKPa = Math.Min(targetManifoldKPa, Math.Max(ambientKPa * 0.5, delivered));
        }

        var demand = new BoostDemandPoint(
            rpm, airFlow, ExhaustFlow(airFlow), turbineInletK, targetManifoldKPa / ambientKPa);

        return new BoostOperatingPoint(rpm, demand, match!, wideOpen!, inlet, manifoldK, choked)
        {
            AmbientKPa = ambientKPa,
            CoolerDropKPa = Spec.CoolerPressureDropKPa,
            TargetBoostKPa = Spec.TargetBoostKPa,
        };
    }

    /// <summary>
    /// The wastegate, at the steady level: where the turbo would overshoot the
    /// target, hold it to the target instead.
    ///
    /// <b>Without this the whole screen draws an engine that never runs.</b>
    /// <see cref="ShaftBalance.Match"/> answers "where does the shaft settle
    /// with nothing bled off", which for any turbo not right at its limit is
    /// well above the boost the user asked for — so the operating line, the
    /// surge and choke margins, the charge temperature and the shaft-speed
    /// check would all be taken at a point the wastegate exists to prevent.
    ///
    /// What the gate physically does is bleed exhaust around the rotor until
    /// the turbine makes exactly the power the compressor needs at the target.
    /// So: find the shaft speed that makes the target pressure ratio at this
    /// flow, then find the expansion ratio at which the turbine produces
    /// exactly that speed's power requirement. The expansion ratio that falls
    /// out is LOWER than the wide-open one, which is the other half of what a
    /// gate does and the half a boost gauge cannot see — opening it gives the
    /// engine its back-pressure back.
    /// </summary>
    private static MatchPoint HoldToTarget(
        Turbocharger turbo,
        MatchPoint wideOpen,
        double airFlowKgPerS,
        RestrictorState inlet,
        double targetManifoldKPa,
        double coolerDropKPa,
        double turbineInletK,
        double turbineOutletKPa)
    {
        // The compressor is asked for the manifold target PLUS the core's
        // drop. Aiming it at the manifold pressure alone leaves the engine
        // short by exactly the drop — which is the same mistake as measuring
        // boost before the intercooler and calling it manifold pressure.
        var targetPressureRatio = (targetManifoldKPa + coolerDropKPa) / inlet.OutletTotalPressureKPa;

        if (wideOpen.Compressor.PressureRatio <= targetPressureRatio || !wideOpen.Converged)
        {
            // The turbo cannot reach the target here, so there is nothing to
            // bleed and the gate is shut. A shortfall is not a control problem.
            return wideOpen;
        }

        var controlledRpm = CompressorModel.SpeedFor(
            turbo.Compressor, targetPressureRatio, airFlowKgPerS,
            inlet.OutletTotalTemperatureK, inlet.OutletTotalPressureKPa);

        if (controlledRpm is not { } rpm)
        {
            return wideOpen;
        }

        var compressor = CompressorModel.Solve(
            turbo.Compressor, airFlowKgPerS, rpm,
            inlet.OutletTotalTemperatureK, inlet.OutletTotalPressureKPa);

        var friction = new BearingFriction();
        var required = compressor.PowerW + friction.PowerW(rpm);

        // Turbine power rises monotonically with expansion ratio at fixed
        // speed, so a bisection between "no expansion at all" and the
        // wide-open ratio is safe and needs no derivative.
        var lo = 1.0;
        var hi = Math.Max(1.0001, wideOpen.ExpansionRatio);

        double PowerAt(double er) =>
            TurbineModel.Solve(turbo.Turbine, er, rpm, turbineInletK, turbineOutletKPa).PowerW
            * turbo.MechanicalEfficiency;

        for (var i = 0; i < 60 && hi - lo > 1e-6; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (PowerAt(mid) < required)
            {
                lo = mid;
            }
            else
            {
                hi = mid;
            }
        }

        var expansionRatio = 0.5 * (lo + hi);

        return new MatchPoint(
            rpm,
            compressor,
            TurbineModel.Solve(turbo.Turbine, expansionRatio, rpm, turbineInletK, turbineOutletKPa),
            expansionRatio,
            friction.PowerW(rpm),
            true);
    }

    /// <summary>Total swept volume, m³.</summary>
    private double Displacement()
    {
        var e = Document.Engine;
        return Math.PI / 4.0 * Math.Pow(e.BoreMm / 1000.0, 2) * (e.StrokeMm / 1000.0) * e.CylinderCount;
    }

    /// <summary>Air plus the fuel burnt with it.</summary>
    private double ExhaustFlow(double airFlowKgPerS) => airFlowKgPerS * (1.0 + (1.0 / AirFuelRatio()));

    private double AirFuelRatio()
    {
        var combustion = Document.Combustion;
        if (combustion is null)
        {
            return 14.7;
        }

        var fuel = FuelLibrary.All.FirstOrDefault(f =>
            f.Name.Contains(combustion.Fuel, StringComparison.OrdinalIgnoreCase));
        return (fuel?.StoichAfr ?? 14.7) * combustion.Lambda;
    }

    /// <summary>
    /// Volumetric efficiency at a speed: measured from the solved sweep where
    /// one exists, and the stated assumption otherwise.
    /// </summary>
    public double VolumetricEfficiencyAt(double rpm)
    {
        if (Run is not { Points.Count: > 0 } run)
        {
            return AssumedVolumetricEfficiency;
        }

        return Interpolate(
            run.Points.Select(p => p.Rpm).ToList(),
            run.Points.Select(p => p.VolumetricEfficiency).ToList(),
            rpm);
    }

    /// <summary>
    /// Turbine inlet temperature at a speed.
    ///
    /// <b>An assumption until a run supplies it.</b> A solved sweep carries a
    /// mass-weighted exhaust temperature per cylinder and that is what gets
    /// used. Without one, this is a placeholder inside the range Heywood
    /// quotes for a spark-ignition engine at wide-open throttle (roughly
    /// 900–1050 °C), rising with speed and falling when the mixture is rich,
    /// because enrichment is exactly what a builder does to protect a turbine.
    /// It is NOT a prediction, every figure that rests on it says so, and it is
    /// replaced the moment a run exists.
    /// </summary>
    public double TurbineInletTemperatureAt(double rpm)
    {
        if (Run is { Points.Count: > 0 } run)
        {
            var withEgt = run.Points.Where(p => p.PerCylinderExhaustTemperatureK.Length > 0).ToList();
            if (withEgt.Count > 0)
            {
                return Interpolate(
                    withEgt.Select(p => p.Rpm).ToList(),
                    withEgt.Select(p => p.PerCylinderExhaustTemperatureK.Average()).ToList(),
                    rpm);
            }
        }

        var lambda = Document.Combustion?.Lambda ?? 1.0;
        var span = Math.Clamp((rpm - 2000.0) / 6000.0, 0.0, 1.0);
        var stoichiometric = 1075.0 + (250.0 * span);

        // Rich mixtures carry the surplus fuel out as unburnt heat sink: about
        // 25 K per 0.01 λ near stoichiometric is the usual rule of thumb.
        return stoichiometric - (2500.0 * (1.0 - Math.Min(lambda, 1.0)));
    }

    private static double Interpolate(IReadOnlyList<double> xs, IReadOnlyList<double> ys, double x)
    {
        if (xs.Count == 0)
        {
            return double.NaN;
        }

        if (xs.Count == 1 || x <= xs[0])
        {
            return ys[0];
        }

        if (x >= xs[^1])
        {
            return ys[^1];
        }

        for (var i = 1; i < xs.Count; i++)
        {
            if (x <= xs[i])
            {
                var t = (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
                return ys[i - 1] + (t * (ys[i] - ys[i - 1]));
            }
        }

        return ys[^1];
    }

    // ---- Figures: Compressor ----------------------------------------------

    /// <summary>
    /// The compressor map with the operating line on it (plan §4.7) — speed
    /// lines, surge and choke lines, and the engine's trajectory across them.
    ///
    /// Extrapolated operating points are drawn as their own series in their
    /// own style, not merged into the line: plan §4.2 requires extrapolated
    /// regions to be visible in every plot, and a point read from outside the
    /// measured envelope presented with the same weight as one inside it is
    /// exactly the failure that rule exists to prevent.
    /// </summary>
    public PlotModel CompressorMapChart()
    {
        if (Turbo is not { } turbo)
        {
            return NoTurbo("Compressor map");
        }

        var map = turbo.Compressor;
        var line = OperatingLine();
        var series = new List<PlotSeries>();

        foreach (var speedLine in map.SpeedLines)
        {
            series.Add(new PlotSeries(
                $"{speedLine.CorrectedRpm / 1000.0:F0}k",
                speedLine.Points.Select(p => p.CorrectedFlowKgPerS).ToList(),
                speedLine.Points.Select(p => p.PressureRatio).ToList(),
                "Brush.BorderSubtle"));
        }

        var surge = map.SurgeLine();
        series.Add(new PlotSeries("Surge line", surge.Select(s => s.Flow).ToList(),
            surge.Select(s => s.PressureRatio).ToList(), "Brush.Warning", PlotSeriesKind.Dashed));

        var choke = map.ChokeLine();
        series.Add(new PlotSeries("Choke line", choke.Select(c => c.Flow).ToList(),
            choke.Select(c => c.PressureRatio).ToList(), "Brush.Info", PlotSeriesKind.Dotted));

        // The operating line in CORRECTED coordinates, because that is the
        // frame the map is drawn in. Behind a restrictor these differ from the
        // actual flows substantially — which is the whole point of §4.6.4.
        var opFlow = line.Select(p => Corrected.Flow(
            p.Demand.AirFlowKgPerS, p.Inlet.OutletTotalTemperatureK, p.Inlet.OutletTotalPressureKPa,
            map.Reference)).ToList();
        var opRatio = line.Select(p => p.Match.Compressor.PressureRatio).ToList();

        series.Add(new PlotSeries("Operating line", opFlow, opRatio, "Brush.Accent"));

        var extrapolated = line
            .Select((p, i) => (p, i))
            .Where(x => x.p.Match.Compressor.IsExtrapolated)
            .ToList();

        if (extrapolated.Count > 0)
        {
            series.Add(new PlotSeries(
                "Extrapolated — outside the measured map",
                extrapolated.Select(x => opFlow[x.i]).ToList(),
                extrapolated.Select(x => opRatio[x.i]).ToList(),
                "Brush.Warning",
                PlotSeriesKind.Scatter));
        }

        var surging = line.Select((p, i) => (p, i)).Where(x => x.p.Match.Compressor.InSurge).ToList();
        if (surging.Count > 0)
        {
            series.Add(new PlotSeries(
                "Past the surge line",
                surging.Select(x => opFlow[x.i]).ToList(),
                surging.Select(x => opRatio[x.i]).ToList(),
                "Brush.Danger",
                PlotSeriesKind.Scatter));
        }

        var allFlow = map.SpeedLines.SelectMany(l => l.Points).Select(p => p.CorrectedFlowKgPerS)
            .Concat(opFlow.Where(double.IsFinite)).ToList();
        var allRatio = map.SpeedLines.SelectMany(l => l.Points).Select(p => p.PressureRatio)
            .Concat(opRatio.Where(double.IsFinite)).ToList();

        var markers = new List<PlotMarker>();
        if (Restrictor is { } restrictor)
        {
            var ceiling = restrictor.ChokedFlow(Ambient.PressurePa, Ambient.TemperatureK);
            var correctedCeiling = Corrected.Flow(
                ceiling, Ambient.TemperatureK, Ambient.PressurePa / 1000.0, map.Reference);
            markers.Add(new PlotMarker(correctedCeiling, "restrictor choke", "Brush.Danger"));
        }

        return new PlotModel
        {
            Title = $"Compressor map — {turbo.Name}",
            Subtitle = $"{Ambient.Label} · {FidelityNote()}",
            XAxis = new PlotAxis("Corrected mass flow", 0, Ceil(allFlow, 0.05), "kg/s"),
            YAxis = new PlotAxis("Pressure ratio", 1.0, Ceil(allRatio, 0.5), ""),
            Series = series,
            Markers = markers,
            Notes = MapNotes(line, map),
        };
    }

    private IReadOnlyList<string> MapNotes(IReadOnlyList<BoostOperatingPoint> line, CompressorMap map)
    {
        var notes = new List<string>();

        if (line.Count > 0)
        {
            var worstSurge = line.Min(p => p.Match.Compressor.SurgeMarginPercent);
            var worstChoke = line.Min(p => p.Match.Compressor.ChokeMarginPercent);
            var weighted = FlowWeightedEfficiency(line);

            notes.Add($"Worst surge margin {worstSurge:F1}%, worst choke margin {worstChoke:F1}% over "
                      + $"{FromRpm:F0}–{ToRpm:F0} rpm. Flow-weighted mean efficiency {weighted:P1}.");
        }

        if (Restrictor is { } restrictor)
        {
            var ceiling = restrictor.ChokedFlow(Ambient.PressurePa, Ambient.TemperatureK);
            notes.Add($"The restrictor sits UPSTREAM of the compressor (plan §4.6.4), so the inlet runs "
                      + $"sub-atmospheric and the whole line shifts right in corrected coordinates. It chokes at "
                      + $"{ceiling:F4} kg/s — no shaft speed, no boost and no cam beats that number.");
        }

        if (line.Any(p => p.Match.Compressor.IsExtrapolated))
        {
            notes.Add("Marked points were read from outside the measured envelope. The extrapolation is physical "
                      + "rather than a spline extension, but it is an extrapolation and is drawn as one.");
        }

        notes.Add($"Speed lines are the map's own, {map.LowestSpeed / 1000.0:F0}k–{map.HighestSpeed / 1000.0:F0}k "
                  + $"corrected rpm against {map.Reference.TemperatureK:F1} K / {map.Reference.PressureKPa:F1} kPa. "
                  + "Reading a map against the wrong reference day is a silent few-percent error, so the reference "
                  + "is stated rather than assumed.");

        return notes;
    }

    public double FlowWeightedEfficiency(IReadOnlyList<BoostOperatingPoint> line)
    {
        var total = line.Sum(p => p.Demand.AirFlowKgPerS);
        return total > 0
            ? line.Sum(p => p.Demand.AirFlowKgPerS * p.Match.Compressor.Efficiency) / total
            : 0.0;
    }

    /// <summary>
    /// Surge and choke margin against engine speed, with the 10% industrial
    /// rule drawn on it. The map shows WHERE the line runs; this shows how
    /// much room it has, which is the number a match is accepted or rejected on.
    /// </summary>
    public PlotModel MarginChart()
    {
        if (Turbo is null)
        {
            return NoTurbo("Surge and choke margin");
        }

        var line = OperatingLine();
        var rpm = line.Select(p => p.EngineRpm).ToList();
        var surge = line.Select(p => p.Match.Compressor.SurgeMarginPercent).ToList();
        var choke = line.Select(p => p.Match.Compressor.ChokeMarginPercent).ToList();
        var required = rpm.Select(_ => 10.0).ToList();

        var all = surge.Concat(choke).Where(double.IsFinite).ToList();

        return new PlotModel
        {
            Title = "Surge and choke margin",
            Subtitle = $"{Ambient.Label} · {FidelityNote()}",
            XAxis = new PlotAxis("Engine speed", FromRpm, ToRpm, "rpm"),
            YAxis = new PlotAxis("Margin", Math.Min(-10, Floor(all, 10)), Math.Max(40, Ceil(all, 10)), "%"),
            Series =
            [
                new PlotSeries("Surge margin", rpm, surge, "Brush.Warning"),
                new PlotSeries("Choke margin", rpm, choke, "Brush.Info", PlotSeriesKind.Dashed),
                new PlotSeries("10% requirement", rpm, required, "Brush.TextSecondary", PlotSeriesKind.Dotted),
            ],
            Notes =
            [
                "Margin is quoted against the flow range's own ends at that speed, which is how every turbo "
                + "datasheet quotes it. Below zero the compressor is past the line, not merely close to it.",
                "10% is the usual industrial rule. A dyno pull and a road car in traffic do not deserve the same "
                + "number — surge that a driver would meet daily is a different problem from surge at one corner "
                + "of a sweep.",
            ],
        };
    }

    // ---- Figures: Turbine -------------------------------------------------

    /// <summary>
    /// The A/R trade curve (plan §4.7), <b>as a front rather than a
    /// recommendation</b>: boost onset against what the housing costs at the
    /// top end.
    ///
    /// <b>Not onset against peak flow.</b> With the wastegate holding the
    /// target, every housing delivers the same boost and therefore the same
    /// air flow at the top of the range — plotting that produces nine points
    /// in a horizontal line and says nothing. What the housing actually
    /// controls up there is BACK-PRESSURE: a small volute makes the engine
    /// pump against a higher exhaust manifold pressure for the same boost,
    /// which is the pumping work that pays for its early onset. So the y axis
    /// is the exhaust-to-intake pressure ratio at the top of the speed range,
    /// and DOWN is better.
    ///
    /// Peak power itself is not on this axis because this model cannot compute
    /// it: pumping work, scavenging and the knock margin that a hotter charge
    /// costs all need the solve. Back-pressure is the term the housing
    /// dominates and the one that carries the sign of the trade.
    /// </summary>
    public PlotModel AreaRatioSweep(double from = 0.40, double to = 1.20, int steps = 9)
    {
        if (Turbo is null)
        {
            return NoTurbo("Turbine A/R sweep");
        }

        var onsets = new List<double>();
        var backPressures = new List<double>();
        var ratios = new List<double>();

        for (var i = 0; i < Math.Max(2, steps); i++)
        {
            var areaRatio = from + ((to - from) * i / (Math.Max(2, steps) - 1.0));
            var line = OperatingLineAt(areaRatio);
            if (line.Count == 0)
            {
                continue;
            }

            ratios.Add(areaRatio);
            onsets.Add(OnsetRpm(line));
            backPressures.Add(line[^1].Match.BackPressureRatio);
        }

        var selectedOnset = OnsetRpm(OperatingLineAt(Spec.TurbineAreaRatio));

        var markers = new List<PlotMarker>
        {
            new(selectedOnset, $"A/R {Spec.TurbineAreaRatio:F2} (selected)", "Brush.Success"),
        };

        return new PlotModel
        {
            Title = "Turbine A/R trade",
            Subtitle = $"Boost onset against top-end back-pressure · {FidelityNote()}",
            XAxis = new PlotAxis("Boost onset", Floor(onsets, 500), Ceil(onsets, 500), "rpm"),
            YAxis = new PlotAxis($"Exhaust ÷ intake at {ToRpm:F0} rpm", Floor(backPressures, 0.25),
                Ceil(backPressures, 0.25), ""),
            Series = [new PlotSeries("A/R front", onsets, backPressures, "Brush.Accent", PlotSeriesKind.Scatter)],
            Markers = markers,
            Notes =
            [
                "Each point is one housing: "
                + string.Join(", ", ratios.Select(r => r.ToString("F2", CultureInfo.InvariantCulture)))
                + ". Left is earlier boost, DOWN is less back-pressure at the top end. Nothing sits in both "
                + "corners, and that is the whole decision.",
                "Peak power is not the y axis because this model cannot compute it — pumping work, scavenging "
                + "and the knock margin a hotter charge costs all need the solve. Back-pressure is the term the "
                + "housing dominates and the one that carries the sign of the trade. Peak air flow would be "
                + "worse than useless here: with the gate holding the target, every housing delivers the same "
                + "flow at the top, so that axis would be flat by construction.",
                "Onset is the lowest speed at which the delivered boost reaches 90% of the target. Where the "
                + "target is never reached, the point sits at the top of the speed range — that is a housing "
                + "this engine cannot use, not a housing with late onset.",
                "Housings are scaled from the measured map by A/R to first order (Watson & Janota, "
                + "Turbocharging the Internal Combustion Engine, 1982). The efficiency field is carried across "
                + "unchanged, so this ranks housings in one family — it does not predict an absolute number for "
                + "a housing nobody measured.",
            ],
        };
    }

    private double OnsetRpm(IReadOnlyList<BoostOperatingPoint> line)
    {
        var reached = line.Where(p => p.DeliveredBoostKPa >= 0.9 * p.TargetBoostKPa).ToList();
        return reached.Count > 0 ? reached.Min(p => p.EngineRpm) : ToRpm;
    }

    /// <summary>
    /// Blade speed ratio against engine speed — whether the turbine is being
    /// run where it can use the energy it is given.
    ///
    /// <c>BSR = U_tip/C_is</c>, and peak total-to-static efficiency sits near
    /// 0.65–0.70 for a radial inflow turbine (plan §4.3). A housing that puts
    /// the turbine far off that is losing energy it already paid for in
    /// back-pressure.
    /// </summary>
    public PlotModel BladeSpeedRatioChart()
    {
        if (Turbo is not { } turbo)
        {
            return NoTurbo("Blade speed ratio");
        }

        if (turbo.Turbine.RotorDiameterM is not { } diameter || diameter <= 0)
        {
            return Empty("Blade speed ratio",
                "This turbine map states no rotor diameter, so BSR is reported as unavailable rather than "
                + "computed from a guessed wheel size.");
        }

        var line = OperatingLine();
        var rpm = line.Select(p => p.EngineRpm).ToList();
        var bsr = line.Select(p =>
        {
            var tipSpeed = Math.PI * diameter * p.Match.ShaftRpm / 60.0;

            // Ideal spouting velocity from the total-to-static expansion the
            // turbine is actually working across.
            const double turbineCp = 1150.0;
            const double turbineGamma = 1.33;
            var drop = p.Demand.TurbineInletK
                       * (1.0 - Math.Pow(1.0 / Math.Max(1.0, p.Match.ExpansionRatio),
                           (turbineGamma - 1.0) / turbineGamma));
            var spouting = Math.Sqrt(Math.Max(0.0, 2.0 * turbineCp * drop));
            return spouting > 1.0 ? tipSpeed / spouting : double.NaN;
        }).ToList();

        var best = rpm.Select(_ => 0.68).ToList();

        return new PlotModel
        {
            Title = "Blade speed ratio",
            Subtitle = $"A/R {Spec.TurbineAreaRatio:F2}, Ø{diameter * 1000.0:F0} mm rotor · {FidelityNote()}",
            XAxis = new PlotAxis("Engine speed", FromRpm, ToRpm, "rpm"),
            YAxis = new PlotAxis("U_tip / C_is", 0, Math.Max(1.2, Ceil(bsr, 0.2)), ""),
            Series =
            [
                new PlotSeries("BSR", rpm, bsr, "Brush.Accent"),
                new PlotSeries("Peak-efficiency BSR", rpm, best, "Brush.Success", PlotSeriesKind.Dashed),
            ],
            Notes =
            [
                "Peak total-to-static efficiency sits near BSR 0.65–0.70 on a radial inflow turbine. Below that "
                + "the wheel is being over-driven by the gas; above it the gas is not arriving fast enough to do "
                + "work on the blades.",
                "This is the STEADY blade speed ratio at each engine speed. A pulse arrives and leaves inside a "
                + "few crank degrees, so the crank-angle-resolved BSR — which is what says whether a manifold is "
                + "delivering its pulse where the turbine can use it — comes from the unsteady solve, not from "
                + "this screen.",
            ],
        };
    }

    // ---- Figures: Control -------------------------------------------------

    /// <summary>
    /// What the turbo would make wide open, what the target is, and therefore
    /// what the wastegate has to throw away — which is the setup decision this
    /// tab exists for.
    /// </summary>
    public PlotModel BoostControlChart()
    {
        if (Turbo is null)
        {
            return NoTurbo("Boost control");
        }

        var line = OperatingLine();
        var rpm = line.Select(p => p.EngineRpm).ToList();
        var delivered = line.Select(p => p.DeliveredBoostKPa).ToList();
        var wideOpen = line.Select(p => p.WideOpenBoostKPa).ToList();
        var target = rpm.Select(_ => Spec.TargetBoostKPa).ToList();
        var spring = rpm.Select(_ => Spec.WastegateSpringKPa).ToList();
        var backPressure = line.Select(p => p.Match.BackPressureRatio).ToList();
        var backPressureWideOpen = line.Select(p => p.WideOpen.BackPressureRatio).ToList();

        var all = delivered.Concat(wideOpen).Concat(target).Where(double.IsFinite).ToList();
        var ratios = backPressure.Concat(backPressureWideOpen).ToList();

        return new PlotModel
        {
            Title = "Boost control",
            Subtitle = $"{Spec.BoostControl} · {Ambient.Label} · {FidelityNote()}",
            XAxis = new PlotAxis("Engine speed", FromRpm, ToRpm, "rpm"),
            YAxis = new PlotAxis("Manifold gauge pressure", 0, Ceil(all, 25), "kPa"),
            RightAxis = new PlotAxis("Exhaust ÷ intake", 0.5, Math.Max(2.5, Ceil(ratios, 0.5)), ""),
            Series =
            [
                new PlotSeries("Delivered (gate controlling)", rpm, delivered, "Brush.Accent"),
                new PlotSeries("Gate shut", rpm, wideOpen, "Brush.Danger", PlotSeriesKind.Dotted),
                new PlotSeries("Target", rpm, target, "Brush.Success", PlotSeriesKind.Dashed),
                new PlotSeries("Wastegate spring", rpm, spring, "Brush.TextSecondary", PlotSeriesKind.Dotted),
                new PlotSeries("Back-pressure, controlled", rpm, backPressure, "Brush.Warning", PlotSeriesKind.Dashed, RightAxis: true),
                new PlotSeries("Back-pressure, gate shut", rpm, backPressureWideOpen, "Brush.Info", PlotSeriesKind.Dotted, RightAxis: true),
            ],
            Notes = ControlNotes(line),
        };
    }

    private IReadOnlyList<string> ControlNotes(IReadOnlyList<BoostOperatingPoint> line)
    {
        var notes = new List<string>();

        if (line.Count > 0)
        {
            var surplus = line.Max(p => p.SurplusKPa);
            var shortfall = line.Min(p => p.SurplusKPa);

            notes.Add(surplus > 1.0
                ? $"At worst the turbo makes {surplus:F0} kPa more than the target and the wastegate has to bleed "
                  + "that off. A gate too small to pass it will not hold boost however stiff its spring."
                : $"The turbo never reaches the target: it is {-shortfall:F0} kPa short at worst. A wastegate "
                  + "cannot help with that — it is a compressor or a turbine-housing decision.");

            var worstBack = line.Max(p => p.Match.BackPressureRatio);
            var worstBackShut = line.Max(p => p.WideOpen.BackPressureRatio);
            notes.Add($"Worst exhaust-to-intake pressure ratio {worstBack:F2} with the gate controlling, against "
                      + $"{worstBackShut:F2} with it shut. Above 1 the engine is pumping against more "
                      + "back-pressure than boost — it costs pumping work and closes the scavenging window, and "
                      + "it is the cost a boost figure on its own hides.");

            notes.Add("Opening the gate does two things, and only one of them is visible on a boost gauge: it "
                      + "stops the boost rising, and it gives the engine its back-pressure back. The second is "
                      + "why a turbo held well below its limit pumps better than its pressure ratio suggests.");
        }

        if (string.Equals(Spec.BoostControl, BoostControlModes.WastegateSpring, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add($"With a spring alone, boost is whatever the exhaust energy produces once the gate cracks at "
                      + $"{Spec.WastegateSpringKPa:F0} kPa — the target is an aspiration, not a setpoint. "
                      + "Closed-loop control holds the target and overshoots on the way to it; that overshoot is a "
                      + "transient, so it belongs to the Transient tab rather than to this steady curve.");
        }

        notes.Add($"Blow-off cracks at {Spec.BlowOffCrackingKPa:F0} kPa differential, "
                  + (Spec.BlowOffRecirculates
                      ? "recirculating to the compressor inlet — quieter, and metered air stays in the system."
                      : "venting to atmosphere — louder, and any air metered before the compressor is lost."));

        return notes;
    }

    // ---- Figures: Charge cooling ------------------------------------------

    /// <summary>
    /// What the compressor does to the charge and what the cooler gives back,
    /// with the density the engine actually breathes on the right-hand axis —
    /// because density is what makes the power, and a temperature on its own
    /// does not say how much was lost.
    /// </summary>
    public PlotModel ChargeCoolingChart()
    {
        if (Turbo is null)
        {
            return NoTurbo("Charge cooling");
        }

        var line = OperatingLine();
        var rpm = line.Select(p => p.EngineRpm).ToList();

        var compressorOut = line.Select(p => p.Match.Compressor.OutletTemperatureK - 273.15).ToList();
        var manifold = line.Select(p => p.ManifoldTemperatureK - 273.15).ToList();
        var ambient = rpm.Select(_ => Ambient.TemperatureK - 273.15).ToList();

        var density = line.Select(p =>
            ((p.DeliveredBoostKPa + p.AmbientKPa) * 1000.0) / (GasConstant * p.ManifoldTemperatureK)).ToList();

        var all = compressorOut.Concat(manifold).Concat(ambient).Where(double.IsFinite).ToList();

        return new PlotModel
        {
            Title = "Charge cooling",
            Subtitle = $"{Spec.ChargeCooler}"
                       + (HasCooler ? $", effectiveness {Spec.CoolerEffectiveness:P0}" : "")
                       + $" · {Ambient.Label} · {FidelityNote()}",
            XAxis = new PlotAxis("Engine speed", FromRpm, ToRpm, "rpm"),
            YAxis = new PlotAxis("Charge temperature", Floor(all, 20), Ceil(all, 20), "°C"),
            RightAxis = new PlotAxis("Charge density", 0, Ceil(density, 0.5), "kg/m³"),
            Series =
            [
                new PlotSeries("Compressor outlet", rpm, compressorOut, "Brush.Warning"),
                new PlotSeries("Into the manifold", rpm, manifold, "Brush.Accent"),
                new PlotSeries("Ambient", rpm, ambient, "Brush.TextSecondary", PlotSeriesKind.Dotted),
                new PlotSeries("Charge density", rpm, density, "Brush.Info", PlotSeriesKind.Dashed, RightAxis: true),
            ],
            Notes = CoolingNotes(line),
        };
    }

    private IReadOnlyList<string> CoolingNotes(IReadOnlyList<BoostOperatingPoint> line)
    {
        var notes = new List<string>();

        if (line.Count > 0)
        {
            var worst = line.MaxBy(p => p.Match.Compressor.OutletTemperatureK)!;
            var recovered = worst.Match.Compressor.OutletTemperatureK - worst.ManifoldTemperatureK;

            notes.Add(HasCooler
                ? $"At {worst.EngineRpm:F0} rpm the compressor delivers {worst.Match.Compressor.OutletTemperatureK - 273.15:F0} °C "
                  + $"and the cooler takes {recovered:F0} K back out, for "
                  + $"{worst.ManifoldTemperatureK - 273.15:F0} °C into the manifold. That recovered temperature is "
                  + $"{recovered / Math.Max(1.0, worst.ManifoldTemperatureK) * 100.0:F1}% of charge density."
                : $"With no charge cooling the manifold sees the full "
                  + $"{worst.Match.Compressor.OutletTemperatureK - 273.15:F0} °C the compressor delivers. That is a "
                  + "choice, and the knock margin is where it gets paid for.");

            notes.Add($"{Spec.CoolerPressureDropKPa:F0} kPa of core pressure drop is charged to the compressor, "
                      + "not to the engine: the compressor is asked for the manifold target PLUS the drop, so a "
                      + "restrictive core shows up here as a higher pressure ratio and a hotter outlet, not just "
                      + "as less boost.");
        }

        if (string.Equals(Spec.ChargeCooler, ChargeCoolerKinds.AirToWater, StringComparison.OrdinalIgnoreCase))
        {
            notes.Add("An air-to-water core carries thermal mass, so it heat-soaks over a run and recovers between "
                      + "runs. That is a transient, and the repeat-run comparison is on the Transient tab.");
        }

        return notes;
    }

    /// <summary>
    /// Heat soak across a sequence of back-to-back pulls (plan §4.7: "a second
    /// dyno pull is not the same as the first").
    ///
    /// The core starts at the coolant temperature and is charged by the air
    /// passing through it, losing heat to the coolant at its own rate. An
    /// air-to-air core on a moving car recovers between runs; the same core on
    /// a dyno with a fan in front of it does not, and the difference is worth
    /// tens of kelvin by the third pull.
    /// </summary>
    public PlotModel HeatSoakChart(int pulls = 4, double pullSeconds = 12.0, double gapSeconds = 25.0)
    {
        if (Turbo is null)
        {
            return NoTurbo("Charge cooler heat soak");
        }

        if (!HasCooler)
        {
            return Empty("Charge cooler heat soak",
                "No charge cooler is fitted, so there is no core to soak. The manifold sees the compressor "
                + "outlet directly at every point of every run.");
        }

        var line = OperatingLine();
        if (line.Count == 0)
        {
            return NoTurbo("Charge cooler heat soak");
        }

        var cooler = new ChargeAirCooler
        {
            RatedEffectiveness = Spec.CoolerEffectiveness,
            RatedFlowKgPerS = Math.Max(0.01, line.Average(p => p.Demand.AirFlowKgPerS)),
            RatedPressureDropPa = Spec.CoolerPressureDropKPa * 1000.0,
        };
        cooler.Reset(Spec.CoolantTemperatureK);

        var hot = line.MaxBy(p => p.Match.Compressor.OutletTemperatureK)!;
        var inletK = hot.Match.Compressor.OutletTemperatureK;
        var flow = hot.Demand.AirFlowKgPerS;

        var times = new List<double>();
        var core = new List<double>();
        var outlet = new List<double>();
        var markers = new List<PlotMarker>();

        const double dt = 0.25;
        var t = 0.0;

        for (var pull = 0; pull < Math.Max(1, pulls); pull++)
        {
            markers.Add(new PlotMarker(t, $"pull {pull + 1}", "Brush.Accent"));

            for (var phase = 0; phase < 2; phase++)
            {
                var underLoad = phase == 0;
                var duration = underLoad ? pullSeconds : gapSeconds;

                for (var elapsed = 0.0; elapsed < duration; elapsed += dt)
                {
                    // Between pulls the engine is idling: a trickle of near-
                    // ambient air, which is what lets the core give its heat
                    // back rather than merely stopping taking more.
                    var state = cooler.Step(
                        dt,
                        underLoad ? inletK : Ambient.TemperatureK + 10.0,
                        underLoad ? flow : flow * 0.08,
                        Spec.CoolantTemperatureK);

                    times.Add(t);
                    core.Add(cooler.CoreTemperatureK - 273.15);
                    outlet.Add(state.OutletTemperatureK - 273.15);
                    t += dt;
                }
            }
        }

        var all = core.Concat(outlet).Where(double.IsFinite).ToList();

        var firstPullPeak = outlet.Take((int)(pullSeconds / dt)).DefaultIfEmpty(0).Max();
        var lastPullStart = (int)((pulls - 1) * (pullSeconds + gapSeconds) / dt);
        var lastPullPeak = outlet.Skip(Math.Min(lastPullStart, Math.Max(0, outlet.Count - 1)))
            .Take((int)(pullSeconds / dt)).DefaultIfEmpty(0).Max();

        return new PlotModel
        {
            Title = "Charge cooler heat soak",
            Subtitle = $"{pulls} × {pullSeconds:F0} s pulls, {gapSeconds:F0} s apart · {Spec.ChargeCooler} "
                       + $"· {FidelityNote()}",
            XAxis = new PlotAxis("Time", 0, Math.Max(1.0, t), "s"),
            YAxis = new PlotAxis("Temperature", Floor(all, 10), Ceil(all, 10), "°C"),
            Series =
            [
                new PlotSeries("Core", times, core, "Brush.Warning"),
                new PlotSeries("Charge leaving the cooler", times, outlet, "Brush.Accent"),
            ],
            Markers = markers,
            Notes =
            [
                $"By pull {pulls} the charge leaves the cooler {lastPullPeak - firstPullPeak:F1} K hotter than it "
                + "did on the first — the same engine, the same boost, less power. That is why a back-to-back "
                + "comparison on a dyno needs the cool-down it never gets.",
                $"The core is modelled as a lumped thermal mass charged by the air through it and discharged to "
                + $"the coolant at {Spec.CoolantTemperatureK - 273.15:F0} °C. It carries no core geometry, so the "
                + "time constant is representative rather than measured.",
            ],
        };
    }

    // ---- Figures: Transient -----------------------------------------------

    /// <summary>
    /// A quasi-steady spool estimate with its sensitivity band.
    ///
    /// <b>What this is.</b> At a fixed engine speed, integrate
    /// <c>J·ω·dω/dt = P_turbine·η_mech − P_compressor − P_friction</c> forward
    /// from a low shaft speed, recomputing the engine's air demand at each step
    /// from the boost the compressor is making at that instant. That is the
    /// standard first-order spool calculation and it runs in milliseconds, so
    /// it can follow a slider.
    ///
    /// <b>What it is not.</b> There is no manifold filling, no pulse energy at
    /// the turbine, no wastegate dynamics and no gas dynamics at all — so it
    /// gets the SHAPE of a spool and the ORDERING between two turbos right,
    /// and it will be optimistic about the first tenth of a second, where
    /// filling a plenum is most of the delay. The coupled answer is
    /// <c>TransientDriver</c> (docs/physics.md §6), which runs the real solve
    /// and is launched from the Run workspace.
    ///
    /// <b>The band is physics, not a percentage.</b> Plan Part 14 gotcha #25:
    /// transient results depend on inertia and friction that users rarely know
    /// accurately. The two bounding curves are the SAME integration with
    /// inertia and bearing friction moved to the ends of the uncertainty the
    /// document declares — whatever spread that produces is what is drawn.
    /// </summary>
    public PlotModel SpoolEstimate(double? atRpm = null)
    {
        if (Turbo is not { } turbo)
        {
            return NoTurbo("Spool estimate");
        }

        var rpm = atRpm ?? TransientRpm;
        var uncertainty = Math.Clamp(Spec.TransientUncertaintyPercent, 0.0, 100.0) / 100.0;

        var nominal = Spool(turbo, rpm, 1.0, 1.0);
        var fast = Spool(turbo, rpm, 1.0 - uncertainty, 1.0 - uncertainty);
        var slow = Spool(turbo, rpm, 1.0 + uncertainty, 1.0 + uncertainty);

        var all = nominal.Boost.Concat(fast.Boost).Concat(slow.Boost).Where(double.IsFinite).ToList();

        var markers = new List<PlotMarker>();
        if (double.IsFinite(nominal.TimeTo90))
        {
            markers.Add(new PlotMarker(nominal.TimeTo90, $"90% at {nominal.TimeTo90:F2} s", "Brush.Success"));
        }

        return new PlotModel
        {
            Title = "Spool estimate",
            Subtitle = $"Step to wide-open at {rpm:F0} rpm · quasi-steady, no gas dynamics",
            XAxis = new PlotAxis("Time", 0, nominal.Times.Count > 0 ? nominal.Times[^1] : 1.0, "s"),

            // Scaled to what actually happened, not to the target. A flat
            // trace on an axis sized for the boost it never made reads as a
            // rendering fault rather than as the answer.
            YAxis = new PlotAxis("Manifold gauge pressure", 0, Math.Max(5.0, Ceil(all, 5)), "kPa"),
            Series =
            [
                new PlotSeries("Nominal", nominal.Times, nominal.Boost, "Brush.Accent"),
                new PlotSeries($"−{uncertainty:P0} inertia and friction", fast.Times, fast.Boost, "Brush.Info", PlotSeriesKind.Dashed),
                new PlotSeries($"+{uncertainty:P0} inertia and friction", slow.Times, slow.Boost, "Brush.Warning", PlotSeriesKind.Dashed),
            ],
            Markers = markers,
            Notes = SpoolNotes(nominal, fast, slow, uncertainty),
        };
    }

    /// <summary>
    /// Engine speed the step-throttle estimate is drawn at. Defaulted low in
    /// the engine's own range, because that is where a spool time is a
    /// complaint rather than a curiosity.
    /// </summary>
    public double TransientRpm { get; set; }

    private IReadOnlyList<string> SpoolNotes(
        SpoolTrace nominal, SpoolTrace fast, SpoolTrace slow, double uncertainty)
    {
        var notes = new List<string>();

        if (double.IsFinite(nominal.TimeTo90))
        {
            var low = Math.Min(fast.TimeTo90, slow.TimeTo90);
            var high = Math.Max(fast.TimeTo90, slow.TimeTo90);
            notes.Add($"Time to 90% of the boost rise: {nominal.TimeTo90:F2} s, in a band of "
                      + $"{low:F2}–{high:F2} s. The band is the same integration with inertia and bearing "
                      + $"friction moved ±{uncertainty:P0}; it is what the physics makes of that uncertainty, not "
                      + "a percentage added to the answer.");
        }
        else
        {
            var reached = nominal.Boost.Count > 0 ? nominal.Boost[^1] - nominal.Boost[0] : 0.0;
            var onset = OperatingLine() is { Count: > 0 } line ? OnsetRpm(line) : double.NaN;

            notes.Add($"This is not a spool: the step reaches {reached:F1} kPa and stops. A turbo sized for the "
                      + "top end has no exhaust energy to work with down here, and quoting a confident time to "
                      + "90% of a rise this small would be arithmetic rather than an answer."
                      + (double.IsFinite(onset) && onset < ToRpm
                          ? $" The steady line first reaches 90% of the target at {onset:F0} rpm; step there to "
                            + "see a spool."
                          : " The steady line never reaches the target anywhere in the speed range either, so "
                            + "this is a matching result rather than a response one."));
        }

        notes.Add("Quasi-steady: the shaft is integrated against the steady power balance at each instant, with "
                  + "the engine's air demand recomputed from the boost it is making. No manifold filling, no "
                  + "pulse energy at the turbine, no wastegate dynamics. It gets the shape and the ordering "
                  + "between turbos right and is optimistic about the first tenth of a second.");

        notes.Add("The coupled transient — gas dynamics, shaft, thermal and compressor stepped together under a "
                  + "scripted throttle, with repeat-run heat soak reaching the boost air — is a solved run, and "
                  + "is what a reported time-to-torque should come from.");

        return notes;
    }

    private sealed record SpoolTrace(IReadOnlyList<double> Times, IReadOnlyList<double> Boost, double TimeTo90);

    private SpoolTrace Spool(Turbocharger turbo, double engineRpm, double inertiaScale, double frictionScale)
    {
        var ambientPa = Ambient.PressurePa;
        var ambientK = Ambient.TemperatureK;
        var ambientKPa = ambientPa / 1000.0;
        var restrictor = Restrictor;
        var friction = new BearingFriction(OilViscosityRatio: Math.Max(0.05, frictionScale));
        var inertia = turbo.ShaftInertia * Math.Max(0.05, inertiaScale);

        var turbineInletK = TurbineInletTemperatureAt(engineRpm);
        var volumetricEfficiency = VolumetricEfficiencyAt(engineRpm);
        var displacement = Displacement();
        var targetManifoldKPa = ambientKPa + Spec.TargetBoostKPa;

        // Off boost, but turning: a shaft at rest would make the first
        // milliseconds a division by zero and is not the state a throttle step
        // starts from anyway.
        var shaftRpm = Math.Max(8_000.0, turbo.Compressor.LowestSpeed * 0.25);

        var times = new List<double>();
        var boost = new List<double>();

        // The engine's air flow, carried from one step to the next. The
        // restrictor has to be solved at the PREVIOUS step's flow, because
        // solving it at a flow not yet computed is how a quasi-steady loop
        // turns into an implicit one nobody wrote.
        var flow = 1e-3;

        const double dt = 0.002;
        const double duration = 3.0;

        for (var t = 0.0; t <= duration; t += dt)
        {
            var inlet = restrictor is null
                ? new RestrictorState(0, ambientKPa, ambientK, 1.0, false, double.PositiveInfinity)
                : restrictor.Solve(flow, ambientPa, ambientK);

            var compressor = CompressorModel.Solve(
                turbo.Compressor, Math.Max(flow, 1e-4), shaftRpm,
                inlet.OutletTotalTemperatureK, inlet.OutletTotalPressureKPa);

            // The wastegate holds the target; nothing invents a shortfall.
            var deliveredKPa = Math.Min(
                targetManifoldKPa,
                (compressor.PressureRatio * inlet.OutletTotalPressureKPa) - Spec.CoolerPressureDropKPa);

            var manifoldK = HasCooler
                ? compressor.OutletTemperatureK
                  - (Spec.CoolerEffectiveness * (compressor.OutletTemperatureK - Spec.CoolantTemperatureK))
                : compressor.OutletTemperatureK;

            var density = deliveredKPa * 1000.0 / (GasConstant * manifoldK);
            flow = volumetricEfficiency * displacement * (engineRpm / 120.0) * density;

            if (restrictor is { } r)
            {
                flow = Math.Min(flow, r.ChokedFlow(ambientPa, ambientK));
            }

            times.Add(t);
            boost.Add(deliveredKPa - ambientKPa);

            var expansionRatio = TurbineModel.ExpansionRatioFor(
                turbo.Turbine, ExhaustFlow(flow), shaftRpm, turbineInletK, Spec.ExhaustBackPressureKPa);
            var turbine = TurbineModel.Solve(
                turbo.Turbine, expansionRatio, shaftRpm, turbineInletK, Spec.ExhaustBackPressureKPa);

            var net = (turbine.PowerW * turbo.MechanicalEfficiency)
                      - compressor.PowerW
                      - friction.PowerW(shaftRpm);

            // J·ω·dω/dt = ΔP, in rpm.
            var omega = shaftRpm * 2.0 * Math.PI / 60.0;
            var domega = net / (inertia * Math.Max(omega, 1.0)) * dt;
            shaftRpm = Math.Max(1_000.0, (omega + domega) * 60.0 / (2.0 * Math.PI));
        }

        var start = boost.Count > 0 ? boost[0] : 0.0;
        var end = boost.Count > 0 ? boost[^1] : 0.0;
        var threshold = start + (0.9 * (end - start));
        var index = boost.FindIndex(b => b >= threshold);

        // A 90% crossing of a rise that is not a spool is not a spool time.
        // Reaching 90% of 0.6 kPa in 0.09 s is arithmetically true and says
        // nothing — a big turbo on a small engine at a low speed simply does
        // not come on boost, and reporting a confident tenth of a second for
        // it is worse than reporting nothing. The bar is a tenth of the
        // target, and never less than 5 kPa.
        var meaningful = Math.Max(5.0, 0.1 * Spec.TargetBoostKPa);
        var crossing = index >= 0 && end - start >= meaningful ? times[index] : double.NaN;

        return new SpoolTrace(times, boost, crossing);
    }

    // ---- Auto-match -------------------------------------------------------

    /// <summary>
    /// Rank the library against this engine (plan §4.7: <i>"Always show the
    /// top five with their trade-offs, never a single 'best'."</i>).
    ///
    /// Every candidate carries its own margins and its own disqualifications,
    /// because "it surges below 3000 rpm" is a trade-off a user may accept and
    /// a ranking that hides it is making the decision for them.
    /// </summary>
    public IReadOnlyList<MatchCandidate> AutoMatch(int top = 5)
    {
        var demand = DemandCurve();
        if (demand.Count == 0)
        {
            return [];
        }

        return TurboLibrary.AsDatabase()
            .Rank(demand, Ambient.TemperatureK, Ambient.PressurePa / 1000.0, Spec.ExhaustBackPressureKPa)
            .Take(Math.Max(1, top))
            .ToList();
    }

    /// <summary>
    /// The engine's requirement, independent of any particular turbo — what
    /// auto-match ranks against.
    ///
    /// Flow is quoted at the TARGET boost rather than at whatever a candidate
    /// delivers, which is what makes it a requirement rather than a result:
    /// every candidate is asked the same question.
    /// </summary>
    public IReadOnlyList<BoostDemandPoint> DemandCurve()
    {
        var ambientKPa = Ambient.PressurePa / 1000.0;
        var targetManifoldKPa = ambientKPa + Spec.TargetBoostKPa;
        var displacement = Displacement();
        var restrictor = Restrictor;

        return Speeds().Select(rpm =>
        {
            var volumetricEfficiency = VolumetricEfficiencyAt(rpm);

            // One pass at an assumed compressor efficiency: the demand curve
            // is a requirement, and refining it against a candidate's own
            // efficiency would make it a different requirement per candidate.
            const double assumedEfficiency = 0.72;
            var pressureRatio = targetManifoldKPa / ambientKPa;
            var outletK = Ambient.TemperatureK
                          * (1.0 + ((Math.Pow(pressureRatio, (Gamma - 1.0) / Gamma) - 1.0) / assumedEfficiency));
            var manifoldK = HasCooler
                ? outletK - (Spec.CoolerEffectiveness * (outletK - Spec.CoolantTemperatureK))
                : outletK;

            var density = targetManifoldKPa * 1000.0 / (GasConstant * manifoldK);
            var airFlow = volumetricEfficiency * displacement * (rpm / 120.0) * density;

            if (restrictor is { } r)
            {
                airFlow = Math.Min(airFlow, r.ChokedFlow(Ambient.PressurePa, Ambient.TemperatureK));
            }

            return new BoostDemandPoint(
                rpm, airFlow, ExhaustFlow(airFlow), TurbineInletTemperatureAt(rpm), pressureRatio);
        }).ToList();
    }

    // ---- Readouts and warnings --------------------------------------------

    /// <summary>Computed values for a tab. Outputs of the model, never inputs.</summary>
    public IReadOnlyList<DerivedReadout> Derived(BoostTab tab) => tab switch
    {
        BoostTab.Compressor => CompressorReadouts(),
        BoostTab.Turbine => TurbineReadouts(),
        BoostTab.Control => ControlReadouts(),
        BoostTab.ChargeCooling => CoolingReadouts(),
        BoostTab.Transient => TransientReadouts(),
        _ => [],
    };

    private IReadOnlyList<DerivedReadout> CompressorReadouts()
    {
        var readouts = new List<DerivedReadout>();
        var ambientKPa = Ambient.PressurePa / 1000.0;

        readouts.Add(new("Target pressure ratio",
            $"{(ambientKPa + Spec.TargetBoostKPa) / ambientKPa:F2}",
            $"{Spec.TargetBoostKPa:F0} kPa gauge over {ambientKPa:F1} kPa ambient."));

        if (Restrictor is { } restrictor)
        {
            var choked = restrictor.ChokedFlow(Ambient.PressurePa, Ambient.TemperatureK);

            // Power from choked flow is an upper bound, not a prediction: it
            // assumes the engine converts every kilogram at a representative
            // BSFC, which is what the rest of the tool exists to compute.
            readouts.Add(new("Restrictor ceiling", $"{choked:F4} kg/s",
                $"Ø{Spec.RestrictorThroatMm:F1} mm at C_d {Spec.RestrictorDischargeCoefficient:F2}. "
                + "Everything else in the induction system is an argument about how close the car gets to this."));

            var line = OperatingLine();
            var chokedAt = line.Where(p => p.RestrictorChoked).Select(p => p.EngineRpm).ToList();
            readouts.Add(chokedAt.Count > 0
                ? new("Choked from", $"{chokedAt.Min():F0} rpm",
                    $"Above this the restrictor, not the turbo, sets the flow. Extra shaft speed becomes pressure "
                    + "ratio against a fixed mass flow — a trajectory straight up the map into surge.",
                    "Every rpm above this point is running the compressor toward surge.")
                : new("Choked from", "never in range",
                    $"The restrictor stays subsonic to {ToRpm:F0} rpm at this boost."));
        }

        if (OperatingLine() is { Count: > 0 } operating)
        {
            readouts.Add(new("Flow-weighted efficiency", $"{FlowWeightedEfficiency(operating):P1}",
                "Weighted by air flow, so the speeds the engine actually spends its air at count for more."));

            var peak = operating.MaxBy(p => p.Match.ShaftRpm)!;
            var rated = Turbo?.Compressor.MaxSpeedRpm;
            readouts.Add(new("Peak shaft speed", $"{peak.Match.ShaftRpm:F0} rpm",
                rated is { } max ? $"Rated {max:F0} rpm ({peak.Match.ShaftRpm / max:P0})." : "The map states no rated limit.",
                rated is { } limit && peak.Match.ShaftRpm > limit ? "Over the rated shaft speed." : null));
        }

        return readouts;
    }

    private IReadOnlyList<DerivedReadout> TurbineReadouts()
    {
        var readouts = new List<DerivedReadout>();
        var line = OperatingLine();

        if (Turbo is { } turbo)
        {
            readouts.Add(new("Housing", $"A/R {Spec.TurbineAreaRatio:F2}",
                turbo.Turbine.AreaRatio is { } baseline
                    ? $"Scaled from the map's own A/R {baseline:F2} by first-order capacity scaling."
                    : "The map states no A/R, so nothing was scaled."));
        }

        if (line.Count > 0)
        {
            readouts.Add(new("Boost onset", $"{OnsetRpm(line):F0} rpm",
                "Lowest speed reaching 90% of the boost target."));

            var worstBack = line.Max(p => p.Match.BackPressureRatio);
            readouts.Add(new("Worst back-pressure ratio", $"{worstBack:F2}",
                "Exhaust manifold pressure ÷ intake manifold pressure.",
                worstBack > 2.0 ? "Above 2: heavy pumping loss and no scavenging window at all." : null));

            var peakExpansion = line.Max(p => p.Match.ExpansionRatio);
            readouts.Add(new("Peak expansion ratio", $"{peakExpansion:F2}",
                "The turbine decides the back-pressure the engine works against — not the other way round."));
        }

        return readouts;
    }

    private IReadOnlyList<DerivedReadout> ControlReadouts()
    {
        var readouts = new List<DerivedReadout>();
        var line = OperatingLine();

        var gate = new Wastegate
        {
            FullOpenAreaM2 = Math.PI / 4.0 * Math.Pow(Spec.WastegateDiameterMm / 1000.0, 2),
            Placement = string.Equals(Spec.WastegatePlacement, "External", StringComparison.OrdinalIgnoreCase)
                ? WastegatePlacement.External
                : WastegatePlacement.Internal,
        };

        readouts.Add(new("Wastegate flow area", $"{gate.FullOpenAreaM2 * 1e6:F0} mm² fully open",
            $"Ø{Spec.WastegateDiameterMm:F0} mm at C_d {gate.DischargeCoefficient:F2}."));

        if (line.Count > 0)
        {
            var surplus = line.Max(p => p.SurplusKPa);
            readouts.Add(new("Worst surplus", $"{surplus:F0} kPa",
                surplus > 0
                    ? "The gate has to bleed this much off, and it can only do that if it has the area for it."
                    : "The turbo never exceeds the target, so the gate never has to hold anything."));
        }

        readouts.Add(new("Scroll division when open",
            $"{gate.ScrollDivisionRetained:P0}",
            gate.Placement == WastegatePlacement.External
                ? "An external gate off the manifold keeps most of a twin-scroll's separation when it opens."
                : "An internal gate dumps into the shared turbine outlet, which collapses a twin-scroll's "
                  + "separation exactly when the engine is making the most pulse energy."));

        return readouts;
    }

    private IReadOnlyList<DerivedReadout> CoolingReadouts()
    {
        var readouts = new List<DerivedReadout>();
        var line = OperatingLine();

        if (!HasCooler)
        {
            readouts.Add(new("Charge cooler", "None",
                "The manifold sees the compressor outlet directly."));
            return readouts;
        }

        readouts.Add(new("Effectiveness", $"{Spec.CoolerEffectiveness:P0}",
            "(T_in − T_out) ÷ (T_in − T_coolant) at the rated flow."));

        if (line.Count > 0)
        {
            var hot = line.MaxBy(p => p.Match.Compressor.OutletTemperatureK)!;
            var recovered = hot.Match.Compressor.OutletTemperatureK - hot.ManifoldTemperatureK;
            var densityGain = hot.ManifoldTemperatureK > 0
                ? (hot.Match.Compressor.OutletTemperatureK / hot.ManifoldTemperatureK) - 1.0
                : 0.0;

            readouts.Add(new("Worst-case recovery", $"{recovered:F0} K",
                $"At {hot.EngineRpm:F0} rpm, where the compressor outlet is hottest."));
            readouts.Add(new("Charge density gained", $"{densityGain:P1}",
                "Density goes as 1/T at fixed pressure, so the temperature the cooler takes out is power."));
            readouts.Add(new("Charged to the compressor", $"{Spec.CoolerPressureDropKPa:F0} kPa",
                "The compressor is asked for the manifold target plus the core's drop, so a restrictive core "
                + "shows up as a hotter outlet as well as less boost."));
        }

        return readouts;
    }

    private IReadOnlyList<DerivedReadout> TransientReadouts()
    {
        var readouts = new List<DerivedReadout>();

        if (Turbo is { } turbo)
        {
            readouts.Add(new("Shaft inertia", $"{turbo.ShaftInertia * 1e6:F2} µkg·m²",
                "The single most important transient number, and the one a datasheet is least likely to state."));
            readouts.Add(new("Uncertainty band", $"±{Spec.TransientUncertaintyPercent:F0}%",
                "Applied to inertia and bearing friction. The band on the answer is whatever the integration "
                + "makes of that — never a percentage added to a time."));
        }

        readouts.Add(new("Coupled transient", "A solved run",
            "This screen's estimate is quasi-steady. Manifold filling, pulse energy at the turbine, wastegate "
            + "dynamics and heat soak reaching the boost air all need the gas-dynamic solve."));

        return readouts;
    }

    /// <summary>
    /// Design warnings, each naming what is wrong, what to do instead, and —
    /// where the cause is a field on another screen — where to go and change
    /// it (plan §8.3: cross-workspace links are first-class).
    /// </summary>
    public IReadOnlyList<DesignWarning> Warnings()
    {
        var warnings = new List<DesignWarning>();

        if (!Spec.IsForced)
        {
            return warnings;
        }

        if (Turbo is null)
        {
            warnings.Add(new(null,
                "No turbocharger is selected, so there is no map to draw an operating line on.",
                "Choose one on the Compressor tab, or let auto-match rank the library against this engine.",
                "plan §4.7"));
            return warnings;
        }

        var line = OperatingLine();
        if (line.Count == 0)
        {
            return warnings;
        }

        var surging = line.Where(p => p.Match.Compressor.InSurge).ToList();
        if (surging.Count > 0)
        {
            warnings.Add(new(null,
                $"The operating line crosses the surge line between {surging.Min(p => p.EngineRpm):F0} and "
                + $"{surging.Max(p => p.EngineRpm):F0} rpm, by up to "
                + $"{-surging.Min(p => p.Match.Compressor.SurgeMarginPercent):F1}%.",
                "A larger plenum between compressor and throttle moves the surge classification; a smaller "
                + "compressor moves the line itself.",
                "plan §4.6.2",
                "Design → Manifold (plenum volume)"));
        }
        else
        {
            var worst = line.Min(p => p.Match.Compressor.SurgeMarginPercent);
            if (worst < 10.0)
            {
                warnings.Add(new(null,
                    $"Surge margin falls to {worst:F1}%, below the 10% usually required.",
                    "Acceptable on a dyno pull, marginal on a road car that meets the same point in traffic.",
                    "plan §4.2",
                    "Design → Manifold (plenum volume)"));
            }
        }

        var choking = line.Where(p => p.Match.Compressor.ChokeMarginPercent < 0).ToList();
        if (choking.Count > 0)
        {
            warnings.Add(new(null,
                $"The compressor runs past its choke line above {choking.Min(p => p.EngineRpm):F0} rpm.",
                "Past choke the wheel cannot pass more air however fast it turns — this is a compressor "
                + "sizing decision, not a boost-control one.",
                "plan §4.2"));
        }
        else
        {
            // Symmetric with the surge side, and it has to be: an operating
            // line that ends its sweep 10% from choke is one engine speed, one
            // cold day or one more point of boost away from the wall, and the
            // efficiency is already falling off there. Warning only once the
            // line is PAST choke would announce the problem after it arrived.
            var worstChoke = line.Min(p => p.Match.Compressor.ChokeMarginPercent);
            if (worstChoke < 10.0)
            {
                var at = line.MinBy(p => p.Match.Compressor.ChokeMarginPercent)!;
                warnings.Add(new(null,
                    $"Choke margin falls to {worstChoke:F1}% at {at.EngineRpm:F0} rpm.",
                    "Efficiency is already dropping toward the choke line, so the top end costs more charge "
                    + "temperature than the map's peak suggests. A larger compressor moves it; more boost does not.",
                    "plan §4.2",
                    "Boost → Charge Cooling"));
            }
        }

        var worstBack = line.Max(p => p.Match.BackPressureRatio);
        if (worstBack > 1.8)
        {
            warnings.Add(new(null,
                $"Exhaust manifold pressure reaches {worstBack:F2}× intake manifold pressure.",
                "A larger turbine housing lowers it at the cost of later onset — the A/R sweep draws that "
                + "trade directly.",
                "plan §4.6.3",
                "Boost → Turbine (A/R sweep)"));
        }

        if (Turbo.MaxTurbineInletK is { } maxTit)
        {
            var hottest = line.Max(p => p.Demand.TurbineInletK);
            if (hottest > maxTit)
            {
                warnings.Add(new(null,
                    $"Turbine inlet temperature reaches {hottest:F0} K against a rated {maxTit:F0} K.",
                    "Enrichment is the usual answer and it costs fuel economy and power; a larger turbine or "
                    + "less boost are the others.",
                    "plan §4.3",
                    "Design → Fuel & Combustion (λ)"));
            }
        }

        if (Turbo.Compressor.MaxSpeedRpm is { } maxSpeed)
        {
            var peak = line.Max(p => p.Match.ShaftRpm);
            if (peak > maxSpeed)
            {
                warnings.Add(new(null,
                    $"Peak shaft speed {peak:F0} rpm exceeds the rated {maxSpeed:F0} rpm.",
                    "The map's own limit, not a modelling one.",
                    "plan §4.2"));
            }
        }

        if (line.Any(p => !p.Match.Converged))
        {
            warnings.Add(new(null,
                "The shaft does not balance anywhere in the searched speed range at one or more points.",
                "The turbine cannot drive this compressor against this demand — a mismatch, not a tuning problem.",
                "plan §4.1"));
        }

        if (!ShowingDocumentAmbient)
        {
            warnings.Add(new(null,
                $"These figures are drawn at {Ambient.Label}, not at this model's own ambient.",
                "The altitude and hot-day views are a check on the match, not a change to the design. The "
                + "model's ambient is edited in Design → Fuel & Combustion.",
                "plan §4.7",
                "Design → Fuel & Combustion (ambient)"));
        }

        return warnings;
    }

    /// <summary>
    /// Every figure this workspace produces, for export-all and the report
    /// generator (plan §7.2). A new plot must be added here too, or it will
    /// be on screen and missing from every export.
    /// </summary>
    public IReadOnlyList<PlotModel> AllPlots()
    {
        if (!Spec.IsForced)
        {
            return [];
        }

        return
        [
            CompressorMapChart(),
            MarginChart(),
            AreaRatioSweep(),
            BladeSpeedRatioChart(),
            BoostControlChart(),
            ChargeCoolingChart(),
            HeatSoakChart(),
            SpoolEstimate(),
        ];
    }

    // ---- Internals --------------------------------------------------------

    private string FidelityNote() => Fidelity switch
    {
        BoostFidelity.Solved => "volumetric efficiency and exhaust temperature from the solved sweep",
        _ => $"instant estimate at an assumed VE of {AssumedVolumetricEfficiency:P0} — a run will refine it",
    };

    private PlotModel NoTurbo(string title) => Empty(title,
        "No turbocharger is selected. Choose one on the Compressor tab, or let auto-match rank the library "
        + "against this engine — every entry is an analytic surface, because shipping a manufacturer's map "
        + "without permission is not something this tool does.");

    private static PlotModel Empty(string title, string why) => new()
    {
        Title = title,
        Subtitle = "nothing to draw",
        XAxis = new PlotAxis("", 0, 1),
        YAxis = new PlotAxis("", 0, 1),
        Notes = [why],
    };

    private static double Ceil(IReadOnlyList<double> values, double step)
    {
        var max = values.Where(double.IsFinite).DefaultIfEmpty(0).Max();
        return Math.Ceiling(max / step) * step;
    }

    private static double Floor(IReadOnlyList<double> values, double step)
    {
        var min = values.Where(double.IsFinite).DefaultIfEmpty(0).Min();
        return Math.Floor(min / step) * step;
    }
}
