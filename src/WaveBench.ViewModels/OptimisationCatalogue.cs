using System.Globalization;
using WaveBench.Model;
using WaveBench.Optimize;

namespace WaveBench.ViewModels;

/// <summary>
/// A model field the optimiser may be pointed at, with bounds that mean
/// something.
///
/// <b>The bounds are the whole value of this type.</b> Plan §9.7: <i>"The
/// optimiser will find unphysical designs that exploit model weaknesses — a
/// 3 m runner, a 2 mm pipe, free scavenging. Constrain aggressively."</i> A
/// user asked to invent bounds for "intake runner length" will guess, and a
/// careless guess is how a search returns something nobody can build. So every
/// field here ships with a range a builder would recognise and, where the
/// world sells discrete sizes, the step it is actually cut to.
/// </summary>
/// <param name="Path">Model path.</param>
/// <param name="Label">Human label.</param>
/// <param name="Minimum">Lower bound in model units — absolute, not relative.</param>
/// <param name="Maximum">Upper bound.</param>
/// <param name="Step">The grid it can be built on, if any.</param>
/// <param name="Why">What moving this field does, for the field's own help text.</param>
public sealed record OptimisableField(
    string Path, string Label, double Minimum, double Maximum, double? Step = null, string Why = "")
{
    /// <summary>
    /// Whether this field belongs in the default suggestion for a model.
    /// A turbine A/R on a naturally aspirated engine is not a variable.
    /// </summary>
    public Func<EngineModelDocument, bool> AppliesTo { get; init; } = _ => true;

    /// <summary>
    /// Whether to suggest it up front. The two or three fields that dominate
    /// an intake are suggested; the rest are available but not assumed, because
    /// a run with twelve variables costs roughly the square of one with three.
    /// </summary>
    public bool Suggested { get; init; }

    /// <summary>
    /// Bounds centred on what the document currently holds, where that is
    /// tighter than the absolute range.
    ///
    /// A user optimising an engine they have already tuned usually wants a
    /// search AROUND it, not across the whole plausible world — and a search
    /// across the whole world spends most of its budget in regions the user
    /// already rejected. The absolute bounds still cap it.
    /// </summary>
    public OptimisationVariable Variable(EngineModelDocument document, double relativeSpan = 0.0)
    {
        ArgumentNullException.ThrowIfNull(document);

        var minimum = Minimum;
        var maximum = Maximum;

        if (relativeSpan > 0 && ModelPath.GetOrDefault(document, Path) is { } raw)
        {
            var current = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (double.IsFinite(current) && current > 0)
            {
                minimum = Math.Max(Minimum, current * (1.0 - relativeSpan));
                maximum = Math.Min(Maximum, current * (1.0 + relativeSpan));
            }
        }

        return new OptimisationVariable(Path, minimum, maximum) { Label = Label, Step = Step };
    }
}

/// <summary>
/// Every field the Optimise workspace offers as a variable (plan §9.1).
///
/// Described as DATA for the same reason <see cref="DesignCatalogue"/> is: a
/// per-field branch in a renderer is a place for a field to go missing, and a
/// variable nobody can select is a capability that does not exist.
/// </summary>
public static class OptimisationCatalogue
{
    /// <summary>Tube sizes a fabricator can actually buy, mm outside diameter.</summary>
    public static IReadOnlyList<double> StockTubeMm { get; } =
        [28.0, 31.8, 34.9, 38.1, 41.3, 44.5, 47.6, 50.8, 54.0, 57.0, 60.3, 63.5];

    public static IReadOnlyList<OptimisableField> Fields { get; } =
    [
        new("IntakeRunner.LengthMm", "Intake runner length", 80, 700, 5,
            "Sets which speed the intake resonance arrives at. The single biggest lever on where the torque "
            + "peak sits, and the reason an engine makes its power where it does.")
        {
            Suggested = true,
        },
        new("IntakeRunner.DiameterMm", "Intake runner Ø", 26, 60, 1,
            "Trades velocity against flow area: narrow fills better low down and strangles the top end, wide "
            + "does the opposite. It interacts strongly with length, so the two are optimised together or not "
            + "at all.")
        {
            Suggested = true,
        },
        new("ExhaustRunner.LengthMm", "Exhaust primary length", 150, 900, 5,
            "Sets when the reflected expansion returns to the exhaust valve. Right, it scavenges the cylinder "
            + "during overlap; wrong, it pushes residuals back in. Also sets the collector arrival timing, so "
            + "it is what the exhaust SOUNDS like.")
        {
            Suggested = true,
        },
        new("ExhaustRunner.DiameterMm", "Exhaust primary Ø", 26, 60, 1,
            "Too small chokes the top end; too large weakens the returning expansion that does the scavenging."),

        new("IntakeValves.CloseDeg", "Intake closes (IVC)", 520, 640, 1,
            "The end of induction, and the strongest cam lever on where the engine breathes. Late IVC fills "
            + "at high speed and blows charge back out at low.")
        {
            AppliesTo = d => d.IntakeValves.MaxLiftMm > 0,
        },
        new("IntakeValves.OpenDeg", "Intake opens (IVO)", 320, 380, 1,
            "With EVC, this sets the overlap — where scavenging happens on a boosted engine and where reversion "
            + "happens on a naturally aspirated one."),
        new("ExhaustValves.OpenDeg", "Exhaust opens (EVO)", 100, 180, 1,
            "Early EVO gives the blowdown pulse more time and costs expansion work on the piston. The trade is "
            + "worth more at high speed than low."),
        new("ExhaustValves.CloseDeg", "Exhaust closes (EVC)", 340, 400, 1,
            "The other half of overlap."),

        new("Combustion.StartDeg", "Spark advance", -40, 0, 0.5,
            "More advance makes more torque until it makes knock instead. Optimise it with a knock-margin "
            + "constraint or the answer will be 'as much as possible'.")
        {
            AppliesTo = d => d.Combustion is not null,
        },
        new("Combustion.Lambda", "λ (relative AFR)", 0.78, 1.05, 0.01,
            "Rich makes power and costs fuel; it also cools the charge, which is how it buys knock margin.")
        {
            AppliesTo = d => d.Combustion is not null,
        },

        new("ForcedInduction.TargetBoostKPa", "Boost target", 20, 250, 5,
            "More boost is more torque until the compressor, the knock margin or the bottom end says otherwise. "
            + "Optimise it against those constraints, never on its own.")
        {
            AppliesTo = d => d.ForcedInduction.IsForced,
        },
        new("ForcedInduction.TurbineAreaRatio", "Turbine A/R", 0.35, 1.30, 0.01,
            "The response-versus-top-end trade in one number: small spools early and back-pressures the top "
            + "end, large does the opposite.")
        {
            AppliesTo = d => d.ForcedInduction.IsForced,
        },
        new("ForcedInduction.CoolerEffectiveness", "Cooler effectiveness", 0.4, 0.92, 0.01,
            "Buys charge density and knock margin, and costs core size and pressure drop.")
        {
            AppliesTo = d => d.ForcedInduction.IsForced
                             && !string.Equals(d.ForcedInduction.ChargeCooler, ChargeCoolerKinds.None,
                                 StringComparison.OrdinalIgnoreCase),
        },
    ];

    public static OptimisableField? Find(string path) =>
        Fields.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Fields that apply to this model at all.
    /// </summary>
    public static IReadOnlyList<OptimisableField> For(EngineModelDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return Fields.Where(f => f.AppliesTo(document) && ModelPath.Exists(document, f.Path)).ToList();
    }

    /// <summary>
    /// The variables a run starts with: the two or three that dominate, not
    /// everything that could matter.
    ///
    /// A twelve-variable search costs roughly the square of a three-variable
    /// one, and the honest default is the small set plus a screening pass to
    /// find out whether the rest are worth adding — which is exactly what
    /// <see cref="Screening"/> is for.
    /// </summary>
    public static IReadOnlyList<OptimisationVariable> Suggested(EngineModelDocument document) =>
        For(document).Where(f => f.Suggested).Select(f => f.Variable(document)).ToList();
}
