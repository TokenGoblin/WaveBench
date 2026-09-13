using WaveBench.Model;

namespace WaveBench.ViewModels;

/// <summary>Boost workspace sub-tabs (plan §8.3).</summary>
public enum BoostTab
{
    Compressor,
    Turbine,
    Control,
    ChargeCooling,
    Transient,
}

/// <summary>
/// One editable forced-induction field, described as DATA for exactly the
/// reason <see cref="DesignField"/> is: a per-field branch in a renderer is a
/// place for a field to go missing, and this screen appears and disappears
/// with the aspiration, so a missing field here is one nobody would notice.
/// </summary>
/// <param name="Path">Model path, e.g. <c>ForcedInduction.TargetBoostKPa</c>.</param>
/// <param name="Label">Human label.</param>
/// <param name="Tab">Which Boost sub-tab it appears on.</param>
/// <param name="Kind">Editing behaviour.</param>
/// <param name="Quantity">Unit family for display conversion.</param>
/// <param name="ModelUnit">Unit the DOCUMENT stores.</param>
/// <param name="Simple">Whether Simple mode surfaces it.</param>
/// <param name="Minimum">Lower plausibility bound in model units, if any.</param>
/// <param name="Maximum">Upper plausibility bound in model units, if any.</param>
/// <param name="Choices">Allowed values for <see cref="FieldKind.Choice"/>.</param>
/// <param name="Help">Why the field exists and what moving it does.</param>
/// <param name="Typical">Where the answer usually lands; numeric fields only.</param>
public sealed record BoostField(
    string Path,
    string Label,
    BoostTab Tab,
    FieldKind Kind = FieldKind.Number,
    Quantity Quantity = Quantity.None,
    string ModelUnit = "",
    bool Simple = false,
    double? Minimum = null,
    double? Maximum = null,
    IReadOnlyList<string>? Choices = null,
    string? Help = null,
    TypicalRange? Typical = null) : IEditableField
{
    /// <summary>
    /// What a user would actually type to find this, when that is not the
    /// label. Boost hardware has more common names than correct ones —
    /// "intercooler", "BOV", "dump valve", "WG" — and a command palette that
    /// only answers to the correct one is a palette that does not answer.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];
}

/// <summary>
/// Every <see cref="ForcedInductionSpec"/> property the Boost workspace edits,
/// in display order.
///
/// <b>Completeness is enforced.</b> The Phase 17 schema walk asks that every
/// editable document property be reachable from SOME workspace; the aspiration
/// selector lives in <see cref="DesignCatalogue"/> and everything else in the
/// block lives here, and the test accepts either. Adding a forced-induction
/// field without giving it a home fails that test.
/// </summary>
public static class BoostCatalogue
{
    /// <summary>The one forced-induction field Design owns — the selector itself.</summary>
    public const string AspirationPath = "ForcedInduction.Aspiration";

    public static IReadOnlyList<(BoostTab Tab, string Title)> Tabs { get; } =
    [
        (BoostTab.Compressor, "Compressor"),
        (BoostTab.Turbine, "Turbine"),
        (BoostTab.Control, "Control"),
        (BoostTab.ChargeCooling, "Charge Cooling"),
        (BoostTab.Transient, "Transient"),
    ];

    public static IReadOnlyList<BoostField> Fields { get; } =
    [
        // ---- Compressor ----------------------------------------------------
        new("ForcedInduction.TurboName", "Turbocharger", BoostTab.Compressor, FieldKind.Choice, Quantity.None, "", true,
            Choices: TurboLibrary.Names,
            Help: "From the turbo library. Projects store the NAME, never the map: a map cannot be redistributed "
                  + "with a shared project (plan §4.7).")
        {
            Aliases = ["turbo", "compressor", "snail"],
        },
        new("ForcedInduction.TargetBoostKPa", "Boost target", BoostTab.Compressor, FieldKind.Number, Quantity.Pressure,
            "kPa", true, 5, 400,
            Help: "Gauge pressure in the intake manifold — what a builder quotes. Absolute pressure ratio follows "
                  + "from it and the ambient.",
            Typical: new TypicalRange(50, 200, "50–100 kPa on a road engine, 150+ on a built race engine"))
        {
            Aliases = ["psi", "bar", "boost"],
        },
        new("ForcedInduction.RestrictorFitted", "Intake restrictor", BoostTab.Compressor, FieldKind.Toggle,
            Quantity.None, "", true,
            Help: "Upstream of the compressor, as the FSAE rules require (plan §4.6.4). It moves the whole "
                  + "operating line across the map.")
        {
            Aliases = ["FSAE", "Formula Student", "venturi", "20 mm"],
        },
        new("ForcedInduction.RestrictorThroatMm", "Restrictor throat Ø", BoostTab.Compressor, FieldKind.Number,
            Quantity.Length, "mm", false, 5, 80,
            Help: "FSAE petrol is 20 mm, E85 19 mm. No boost and no cam beats the flow this chokes at.",
            Typical: new TypicalRange(19, 26, "a rulebook fixes it; FSAE is 20 mm on petrol and 19 mm on E85")),
        new("ForcedInduction.RestrictorDischargeCoefficient", "Restrictor C_d", BoostTab.Compressor, FieldKind.Number,
            Quantity.None, "", false, 0.5, 1.0,
            Help: "0.95–0.98 for a properly made venturi; a sharp-edged orifice is far worse.",
            Typical: new TypicalRange(0.93, 0.98, "the difference between the ends of this range is real power")),
        new("ForcedInduction.RestrictorDiffuserRecovery", "Diffuser recovery", BoostTab.Compressor, FieldKind.Number,
            Quantity.None, "", false, 0.0, 1.0,
            Help: "Fraction of the dynamic head the diffuser gives back. The cheapest few kilowatts on the car.",
            Typical: new TypicalRange(0.4, 0.8, "a long, shallow-angle diffuser recovers most; an abrupt one nothing")),

        // ---- Turbine -------------------------------------------------------
        new("ForcedInduction.TurbineAreaRatio", "Turbine A/R", BoostTab.Turbine, FieldKind.Number, Quantity.None, "m",
            true, 0.2, 2.0,
            Help: "Volute area ratio. Small spools early and chokes the top end; large does the opposite. "
                  + "The sweep on this tab draws that trade directly.",
            Typical: new TypicalRange(0.4, 1.2, "the range the A/R sweep covers, and where catalogue housings sit"))
        {
            Aliases = ["A/R", "housing", "hot side", "volute"],
        },
        new("ForcedInduction.ExhaustBackPressureKPa", "Post-turbine pressure", BoostTab.Turbine, FieldKind.Number,
            Quantity.Pressure, "kPa", false, 50, 300,
            Help: "Absolute, downstream of the turbine — ambient plus whatever the exhaust system adds.",
            Typical: new TypicalRange(95, 135, "ambient for an open downpipe; a restrictive system adds 10–30 kPa")),

        // ---- Control -------------------------------------------------------
        new("ForcedInduction.BoostControl", "Boost control", BoostTab.Control, FieldKind.Choice, Quantity.None, "",
            false, Choices: BoostControlModes.All,
            Help: "A spring alone gives whatever boost the exhaust energy produces above cracking; closed loop "
                  + "holds the target and overshoots on the way there."),
        new("ForcedInduction.WastegateSpringKPa", "Wastegate spring", BoostTab.Control, FieldKind.Number,
            Quantity.Pressure, "kPa", false, 10, 300,
            Help: "Gauge pressure at which the gate starts to crack open. On spring-only control it is what sets "
                  + "the boost the engine actually sees.",
            Typical: new TypicalRange(30, 150, "usually chosen at or a little below the boost target"))
        {
            Aliases = ["WG", "spring", "wastegate"],
        },
        new("ForcedInduction.WastegateDiameterMm", "Wastegate Ø", BoostTab.Control, FieldKind.Number, Quantity.Length,
            "mm", false, 10, 80,
            Help: "Port diameter. Too small and the gate cannot hold boost at high flow whatever the spring.",
            Typical: new TypicalRange(28, 46, "internal gates at the bottom; external gates are larger")),
        new("ForcedInduction.WastegatePlacement", "Wastegate placement", BoostTab.Control, FieldKind.Choice,
            Quantity.None, "", false, Choices: ["Internal", "External"],
            Help: "External gates off the manifold keep more of a twin-scroll's division when open (plan §4.5)."),
        new("ForcedInduction.BlowOffCrackingKPa", "Blow-off cracking", BoostTab.Control, FieldKind.Number,
            Quantity.Pressure, "kPa", false, 5, 200,
            Help: "Differential across the valve at which it starts to open on a closed throttle. Set it too high "
                  + "and the trapped charge surges the compressor instead of venting.",
            Typical: new TypicalRange(15, 60, "well below the boost target, or it never opens in time"))
        {
            Aliases = ["BOV", "dump valve", "blow off", "recirc"],
        },
        new("ForcedInduction.BlowOffRecirculates", "Recirculating blow-off", BoostTab.Control, FieldKind.Toggle,
            Quantity.None, "", false,
            Help: "Back to the compressor inlet rather than to atmosphere. Quieter, and it keeps metered air in "
                  + "the system."),

        // ---- Charge cooling ------------------------------------------------
        new("ForcedInduction.ChargeCooler", "Charge cooler", BoostTab.ChargeCooling, FieldKind.Choice, Quantity.None,
            "", true, Choices: ChargeCoolerKinds.All,
            Help: "Air-to-air follows ambient; air-to-water carries a core that heat-soaks and recovers between runs.")
        {
            Aliases = ["intercooler", "IC", "CAC", "chargecooler"],
        },
        new("ForcedInduction.CoolerEffectiveness", "Effectiveness", BoostTab.ChargeCooling, FieldKind.Number,
            Quantity.None, "", true, 0.0, 1.0,
            Help: "(T_in − T_out)/(T_in − T_coolant) at the rated flow. 0.7–0.85 is a good road core.",
            Typical: new TypicalRange(0.6, 0.9, "1.0 would mean the charge leaves at coolant temperature — nothing does"))
        {
            Aliases = ["intercooler", "IC"],
        },
        new("ForcedInduction.CoolerPressureDropKPa", "Core pressure drop", BoostTab.ChargeCooling, FieldKind.Number,
            Quantity.Pressure, "kPa", false, 0, 60,
            Help: "At the rated flow. Boost measured before the core is boost the engine never sees.",
            Typical: new TypicalRange(3, 20, "a generously sized bar-and-plate core at the bottom")),
        new("ForcedInduction.CoolantTemperatureK", "Coolant temperature", BoostTab.ChargeCooling, FieldKind.Number,
            Quantity.Temperature, "K", false, 250, 400,
            Help: "The cold side. Ambient air for an air-to-air core.",
            Typical: new TypicalRange(285, 330, "ambient for air-to-air; an air-to-water loop runs warmer under load")),

        // ---- Transient -----------------------------------------------------
        new("ForcedInduction.TransientUncertaintyPercent", "Inertia/friction uncertainty", BoostTab.Transient,
            FieldKind.Number, Quantity.None, "%", false, 0, 100,
            Help: "How far shaft inertia and bearing friction are allowed to move for the sensitivity band. "
                  + "The band is whatever the physics makes of this — never an invented ± on the answer.",
            Typical: new TypicalRange(10, 30, "20% is honest for a catalogue turbo with no measured rotor")),
    ];

    public static IReadOnlyList<BoostField> For(BoostTab tab) =>
        Fields.Where(f => f.Tab == tab).ToList();

    public static BoostField? Find(string path) =>
        Fields.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
}
