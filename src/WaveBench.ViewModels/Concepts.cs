namespace WaveBench.ViewModels;

/// <summary>
/// One short explainer (plan §8.9: <i>"Concepts panel — short explainers with
/// diagrams (wave tuning, Helmholtz resonance, discharge coefficients, engine
/// orders, surge, blade speed ratio), linked from the fields they govern"</i>).
///
/// <b>The Fields list is the link, and it points both ways.</b> A concept
/// nobody can reach from the field it explains is a glossary; a field with no
/// concept behind it is a number with a tooltip. Naming the paths here means
/// the Design and Boost screens can offer "what is this?" beside the right
/// rows without either catalogue knowing that concepts exist.
/// </summary>
/// <param name="Id">Stable identifier, used by the command palette and deep links.</param>
/// <param name="Title">What it is called.</param>
/// <param name="Summary">One sentence, for the field tooltip and the search result.</param>
/// <param name="Body">
/// The explainer itself, in paragraphs. Plain text: this is view-model data
/// and the renderer decides what a paragraph looks like.
/// </param>
/// <param name="Fields">Catalogue paths this concept governs.</param>
/// <param name="Citation">Where to read more, where a correlation is involved.</param>
/// <param name="Figure">
/// A plot the concept is illustrated by, named as <c>Workspace → Sub-tab</c>
/// the same way a <see cref="WarningLink"/> is. Null where the explainer
/// stands on its own.
/// </param>
public sealed record Concept(
    string Id,
    string Title,
    string Summary,
    IReadOnlyList<string> Body,
    IReadOnlyList<string> Fields,
    string? Citation = null,
    WarningLink? Figure = null);

/// <summary>
/// The Concepts panel's content. Data, like every other catalogue here, so a
/// new explainer is a list entry rather than a new screen.
/// </summary>
public static class ConceptLibrary
{
    public static IReadOnlyList<Concept> All { get; } =
    [
        new("wave-tuning", "Wave tuning",
            "A pressure wave sent down a pipe comes back, and what it does when it arrives depends on how long "
            + "the pipe is and how fast the engine is turning.",
            [
                "Every time a valve opens or closes it launches a pressure wave down the pipe attached to it. That "
                + "wave travels at the speed of sound in the gas — about 340 m/s in cool intake air, and 550–650 m/s "
                + "in hot exhaust, which is why the two sides are tuned to different lengths.",

                "At the far end the wave reflects. An open end reflects it INVERTED: a compression wave comes back "
                + "as a rarefaction. A closed end reflects it unchanged. That sign is the whole mechanism. On the "
                + "intake, the rarefaction launched when the valve opens runs to the plenum, returns as a "
                + "compression, and if it arrives just before the valve closes it packs extra charge in. On the "
                + "exhaust, the compression launched by blowdown returns as a rarefaction, and if it arrives during "
                + "overlap it pulls the residuals out and draws fresh charge in behind them.",

                "Because the wave's travel time is fixed by the pipe and the gas, and the valve timing is fixed by "
                + "the crank, the two only line up at particular engine speeds. That is why a tuned length has a "
                + "speed: it is not a length that is good, it is a length that is good AT an rpm. Longer tunes "
                + "lower, shorter tunes higher, and the peak you gain at one end you pay for at the other.",

                "The x–t wave diagram in Results shows this directly: distance along the pipe on one axis, crank "
                + "angle on the other, and the waves as diagonal streaks whose slope is the speed of sound.",
            ],
            ["IntakeRunner.LengthMm", "ExhaustRunner.LengthMm", "IntakeRunner.DiameterMm", "ExhaustRunner.DiameterMm"],
            "Blair, Design and Simulation of Four-Stroke Engines, ch. 6",
            WarningLink.Plot(Workspace.Results, "Waves")),

        new("helmholtz", "Helmholtz resonance",
            "A plenum and a runner behave like a mass of air bouncing on a spring, and that has its own natural "
            + "frequency independent of any wave travelling.",
            [
                "Blow across a bottle and it sounds a note. The air in the neck is the mass; the air in the body is "
                + "the spring. An intake runner opening into a plenum is the same arrangement, and it resonates at "
                + "f = (c/2π)·√(A / (V·L_eff)), where A and L are the runner's area and effective length and V is "
                + "the volume it opens into.",

                "This is a DIFFERENT mechanism from wave tuning, and both are present at once. Wave tuning depends "
                + "on travel time and cares about the sign of a reflection; Helmholtz resonance depends on a "
                + "volume and does not involve a wave travelling anywhere. On a single-cylinder engine with a "
                + "large airbox the Helmholtz effect usually dominates; on a long individual-runner manifold the "
                + "wave effect does.",

                "The effective length is longer than the physical one — the air just outside each end moves with "
                + "the plug in the neck. The usual end correction is about 0.6 of the radius at a flanged end and "
                + "0.85 at a free one, which on a 40 mm runner is 12–17 mm per end. On a short runner that is "
                + "several per cent of the answer.",
            ],
            ["IntakeRunner.LengthMm", "IntakeRunner.DiameterMm"],
            "Blair, Design and Simulation of Four-Stroke Engines, §6.2"),

        new("discharge-coefficient", "Discharge coefficients",
            "A real restriction passes less than its area suggests, and the shortfall is what a discharge "
            + "coefficient measures.",
            [
                "Flow through a hole does not fill the hole. It separates at the edge and contracts to a smaller "
                + "jet — the vena contracta — so the effective area is less than the geometric one. C_d is the "
                + "ratio: the flow you actually get over the flow an ideal isentropic nozzle of that area would "
                + "pass.",

                "It is not a constant. A valve's C_d changes with lift, with the pressure ratio across it, and "
                + "with which direction the flow is going — an exhaust valve flowing backwards is not the same "
                + "restriction as one flowing forwards. Measured flow-bench data always beats a generic curve, and "
                + "where this model is using a generic one it says so rather than quietly presenting it as "
                + "measured.",

                "A well-made venturi reaches 0.95–0.98 because the contraction is gradual and the diffuser gives "
                + "the velocity head back. A sharp-edged orifice of the same throat area is nearer 0.6. That is "
                + "the difference between a restrictor that costs a few per cent and one that costs a third of "
                + "the power.",
            ],
            ["IntakeValves.ThroatDiameterMm", "ExhaustValves.ThroatDiameterMm",
             "ForcedInduction.RestrictorDischargeCoefficient", "ForcedInduction.RestrictorDiffuserRecovery"],
            "Heywood, Internal Combustion Engine Fundamentals, §6.3"),

        new("engine-orders", "Engine orders",
            "Engine noise is not spread evenly across frequency: it arrives in harmonics of the crankshaft, and "
            + "which ones are loud is decided by the firing pattern.",
            [
                "An order is a multiple of shaft speed. First order is once per revolution; on a four-stroke "
                + "four-cylinder the firing order is 2nd, because four cylinders fire over two revolutions. A V8 "
                + "fires 4th. Halves appear on odd-cylinder engines and on anything whose cylinders do not fire "
                + "evenly.",

                "Because orders track engine speed, they draw straight lines in a spectrogram of an rpm sweep, "
                + "with a slope equal to the order number. A resonance, by contrast, sits at a FIXED frequency and "
                + "draws a horizontal line. That is how the two are told apart on sight, and why a spectrogram is "
                + "the right figure for exhaust noise rather than a single spectrum.",

                "What a listener calls the character of an exhaust is mostly the relative height of the firing "
                + "order and its neighbours. An engine dominated by its firing order sounds smooth and hard; one "
                + "with strong half-orders sounds lumpy, which is exactly what an uneven firing interval produces.",
            ],
            [],
            "plan §3.3",
            WarningLink.Plot(Workspace.Sound, "Spectrum")),

        new("surge", "Compressor surge",
            "Below a certain flow a centrifugal compressor cannot hold its pressure ratio, and the flow reverses — "
            + "violently, and repeatedly.",
            [
                "A compressor raises pressure by turning velocity into pressure in the diffuser. At low flow the "
                + "angle the air meets the blades at goes wrong, the boundary layer separates, and the wheel "
                + "briefly cannot hold the pressure that is already downstream of it. The flow reverses, the "
                + "downstream pressure falls, forward flow resumes, and the cycle repeats — typically at 5–30 Hz, "
                + "audible as fluttering or barking.",

                "The surge line on a compressor map is the locus of those points. An operating line drawn on the "
                + "map has to stay to the RIGHT of it with margin, because the line drawn is one steady-state "
                + "condition and a real engine meets that point on a closing throttle too.",

                "The classic cause is a compressor too large for the engine: a big wheel asked for a modest mass "
                + "flow at a real pressure ratio sits far left on its own map. The classic trigger is a closed "
                + "throttle with the compressor still spinning, which is what a blow-off valve exists to prevent.",
            ],
            ["ForcedInduction.TurboName", "ForcedInduction.TargetBoostKPa", "ForcedInduction.BlowOffCrackingKPa"],
            "Watson & Janota, Turbocharging the Internal Combustion Engine, ch. 4",
            WarningLink.Plot(Workspace.Boost, "Compressor")),

        new("blade-speed-ratio", "Blade speed ratio",
            "A turbine is efficient over a narrow band of tip speed relative to the gas speed available to it, "
            + "and that ratio is what a housing size really selects.",
            [
                "U/C is the wheel's tip speed over the theoretical spouting velocity — the speed the gas would "
                + "reach expanding isentropically through the same pressure ratio. A radial turbine peaks near "
                + "U/C ≈ 0.7, and falls away either side of it.",

                "That single number explains most of what a turbine housing does. A small A/R accelerates the gas "
                + "more for the same mass flow, which lowers C's denominator role and lifts U/C at low engine "
                + "speed — so the turbine works efficiently sooner and the turbo spools early. At high flow the "
                + "same housing chokes and the pressure upstream of it climbs, which the engine pays for in "
                + "pumping work.",

                "A large A/R does the opposite: poor U/C and lazy spool low down, and a much freer top end. There "
                + "is no housing that is good at both, which is why the A/R sweep is drawn as a trade rather than "
                + "resolved into a recommendation.",
            ],
            ["ForcedInduction.TurbineAreaRatio", "ForcedInduction.ExhaustBackPressureKPa"],
            "Baines, Fundamentals of Turbocharging, ch. 5",
            WarningLink.Plot(Workspace.Boost, "Turbine")),

        new("valve-overlap", "Valve overlap",
            "The window where both valves are open at once is where an exhaust pipe gets to help the intake — or "
            + "gets to ruin it.",
            [
                "Around firing TDC the exhaust valve has not yet closed and the intake has already opened. What "
                + "happens in that window depends entirely on the pressures either side of the cylinder. If the "
                + "exhaust port is below the intake — which a correctly tuned primary arranges, by returning a "
                + "rarefaction at that moment — the residuals are pulled out and fresh charge follows them in.",

                "If the pressures are the wrong way round, the same overlap pushes exhaust gas back into the "
                + "intake port. That is why a big-overlap cam is fast at one engine speed and undriveable at idle: "
                + "the mechanism has not changed, only whether the wave arrives at the right moment.",

                "On a boosted engine overlap also lets boost air blow straight through to the exhaust. That "
                + "scavenges the chamber and cools the turbine, and it costs the fuel that went out with it — "
                + "which this model charges to the answer rather than treating as free.",
            ],
            ["IntakeValves.OpenDeg", "ExhaustValves.CloseDeg"],
            "Heywood, Internal Combustion Engine Fundamentals, §6.2"),

        new("knock", "Knock",
            "The unburned charge ahead of the flame can ignite on its own, and the pressure wave that follows "
            + "destroys pistons.",
            [
                "As the flame front advances it compresses whatever is ahead of it. That end gas is hot and under "
                + "pressure, and if it is left there long enough it will autoignite without waiting for the flame. "
                + "The result is a near-instantaneous release across the remaining charge, and a pressure wave "
                + "that rings the chamber at several kilohertz — the audible knock.",

                "Whether it happens is a race between the flame arriving and the end gas's own ignition delay "
                + "expiring. The Livengood–Wu integral is how that race is scored: accumulate dt/τ over the cycle, "
                + "and onset is where the sum reaches 1.",

                "Everything that makes an engine efficient makes knock more likely — more compression, more boost, "
                + "more advance, hotter intake air. Everything that resists it costs something: less advance costs "
                + "efficiency directly, a richer mixture costs fuel, and a better fuel costs money. This model "
                + "ranks fuels against each other reliably; it does not claim an absolute onset.",
            ],
            ["Engine.CompressionRatio", "Combustion.StartDeg", "Combustion.Lambda", "Combustion.TrackKnock"],
            "Livengood & Wu, 5th Symposium on Combustion, 1955"),

        new("charge-cooling", "Charge cooling",
            "Compressing air heats it, and hot air is thin — so a compressor that makes boost without a cooler "
            + "gives back a large part of what it made.",
            [
                "Compression raises temperature as well as pressure, and a real compressor raises it more than an "
                + "ideal one because its inefficiency all ends up as heat in the air. At a pressure ratio of 2 and "
                + "72% efficiency, ambient air at 25 °C leaves the compressor around 115 °C.",

                "Density goes as P/T. That 2.0 pressure ratio, undone by the temperature rise, delivers a density "
                + "ratio nearer 1.55 — so nearly a quarter of the boost has been spent heating the air rather than "
                + "packing it. A cooler that recovers most of that temperature rise recovers most of the loss.",

                "Effectiveness is the fraction of the available temperature drop the core achieves: "
                + "(T_in − T_out)/(T_in − T_coolant). It is never 1, and it falls as flow rises. The core also "
                + "costs pressure, which is why boost measured before it is boost the engine never sees.",
            ],
            ["ForcedInduction.ChargeCooler", "ForcedInduction.CoolerEffectiveness",
             "ForcedInduction.CoolerPressureDropKPa", "ForcedInduction.CoolantTemperatureK"],
            "Watson & Janota, Turbocharging the Internal Combustion Engine, ch. 3",
            WarningLink.Plot(Workspace.Boost, "Charge Cooling")),

        new("volumetric-efficiency", "Volumetric efficiency",
            "How much air the engine actually swallows, against how much its swept volume says it should — the "
            + "single number most of this tool is really computing.",
            [
                "VE is the mass of fresh charge trapped in the cylinder over the mass that would fill the swept "
                + "volume at inlet conditions. Torque follows it almost proportionally, because the fuel that can "
                + "be burned is set by the air that arrived.",

                "A naturally aspirated engine typically runs 0.80–0.95 over most of its range, and a well-tuned "
                + "one exceeds 1.0 near its tuned speed — the ram and wave effects genuinely pack in more than the "
                + "displacement. That is not a modelling error; it is the thing intake tuning is for.",

                "Under boost VE is quoted against INLET conditions, not ambient, so a turbo engine's VE is not "
                + "2.0 because it runs 1 bar of boost. It stays around 0.9 and the density does the rest.",
            ],
            ["IntakeValves.CloseDeg", "IntakeRunner.LengthMm", "IntakeValves.MaxLiftMm"],
            "Heywood, Internal Combustion Engine Fundamentals, §2.10",
            WarningLink.Plot(Workspace.Results, "Performance")),
    ];

    public static Concept? Find(string id) =>
        All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The concepts that explain one field — the link the plan asks for, read
    /// from the concept side so neither catalogue has to know about this file.
    /// </summary>
    public static IReadOnlyList<Concept> For(string path) =>
        All.Where(c => c.Fields.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase))).ToList();

    /// <summary>Substring search over title, summary and body, for the global search box.</summary>
    public static IReadOnlyList<Concept> Search(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return All;
        }

        return All.Where(c =>
            c.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || c.Summary.Contains(query, StringComparison.OrdinalIgnoreCase)
            || c.Body.Any(p => p.Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
    }
}
