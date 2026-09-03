namespace WaveBench.ViewModels;

/// <summary>
/// Real engines, hand-verified against their cited source before being added
/// here (plan-adjacent — not part of the 26-phase build contract, but held to
/// the same rule as <c>TurboDatabase</c>: no entry without a source and a
/// licence, and no figure this file doesn't show being read from that
/// source).
///
/// <b>A starting batch, not a scrape.</b> Wikipedia's own infoboxes carry
/// only bore, stroke, displacement, compression ratio (sometimes), valve
/// count and a peak-power rpm — never the duct lengths, cam events or
/// combustion timing a runnable model needs, so every entry here is a partial
/// fact set that <see cref="EngineEntry.Seed"/> completes by wizard
/// derivation, not a claim of a fully measured engine.
///
/// One correction worth recording: the original request for this feature
/// named "VW 07K" as an example, on the assumption that it designates the
/// EA888 2.0 TSI. It does not — 07K is VW/Audi's parts-code prefix for the
/// EA855-family 2.5-litre inline-five (the naturally aspirated Jetta/Rabbit/
/// Beetle five, and, confusingly, the unrelated-in-tune-but-same-block 2.5
/// TFSI turbo five in the RS3/TT RS). Wikipedia's own "List of Volkswagen
/// Group petrol engines" documents the EA855 R5 section for the TURBOCHARGED
/// TFSI state of tune (228–294 kW, 10.0:1 CR) and never states a compression
/// ratio for the naturally aspirated 07K variant specifically — so it is left
/// out of this batch rather than guessed. The EA888 2.0 TSI entry below is
/// real and well-sourced; it is just not what "07K" actually names.
/// </summary>
public static class EngineLibrary
{
    public static IReadOnlyList<EngineEntry> Curated { get; } =
    [
        new EngineEntry
        {
            Name = "BMW S54B32",
            Manufacturer = "BMW",
            Code = "S54B32",
            Family = "S54",
            BoreMm = 87.0,
            StrokeMm = 91.0,
            CompressionRatio = 11.5,
            CylinderCount = 6,
            Aspiration = EngineAspiration.NaturallyAspirated,
            DisplacementCc = 3246.0,
            PeakPowerRpm = 7900.0,
            Source = "Wikipedia, \"BMW S54\", https://en.wikipedia.org/wiki/BMW_S54",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, compression ratio and peak-power "
                + "rpm are bare numeric fact taken from the article's infobox, not copied prose.",
            Tags = ["BMW", "S5x", "straight-six", "naturally-aspirated", "E46 M3", "Z4M", "tuner-popular"],
        },

        new EngineEntry
        {
            Name = "Volkswagen/Audi EA888 Gen 3 2.0 TSI",
            Manufacturer = "Volkswagen/Audi",
            Family = "EA888 Gen 3",
            BoreMm = 82.5,
            StrokeMm = 92.8,
            CompressionRatio = 9.6,
            CylinderCount = 4,
            ValveCountPerCylinder = 4,
            Aspiration = EngineAspiration.Turbocharged,
            DisplacementCc = 1984.0,
            Source = "Wikipedia, \"Volkswagen EA888 engine\", https://en.wikipedia.org/wiki/Volkswagen_EA888_engine",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, compression ratio and valve count "
                + "are bare numeric fact taken from the article, not copied prose.",
            Tags = ["Volkswagen", "Audi", "EA888", "inline-four", "turbocharged", "MQB", "tuner-popular"],

            // No peak-power rpm cited: the article lists many regional/state-of-tune
            // variants (e.g. CJXA/CJXB/CJXC...) without a single figure that applies
            // to the family as a whole, and picking one would misrepresent it as THE
            // EA888's number. EngineEntry.Seed()'s FallbackTargetRpm applies instead.

            // NOTE: this is NOT the "07K" engine the original feature request named
            // as an example — see the class-level remarks on EngineLibrary.
        },

        new EngineEntry
        {
            Name = "Toyota 2JZ-GTE",
            Manufacturer = "Toyota",
            Code = "2JZ-GTE",
            Family = "JZ",
            BoreMm = 86.0,
            StrokeMm = 86.0,
            CylinderCount = 6,
            ValveCountPerCylinder = 4,
            Aspiration = EngineAspiration.Turbocharged,
            DisplacementCc = 2997.0,
            PeakPowerRpm = 6000.0,
            Source = "Wikipedia, \"Toyota JZ engine\", https://en.wikipedia.org/wiki/Toyota_JZ_engine",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, valve count and peak-power rpm are "
                + "bare numeric fact taken from the article, not copied prose.",
            Tags = ["Toyota", "JZ", "straight-six", "twin-turbo", "Supra", "tuner-popular"],

            // No compression ratio cited: the article states only that the GTE's
            // recessed piston tops give a LOWER ratio than the 2JZ-GE's, without
            // naming either number. EngineEntry.Seed()'s FallbackCompressionRatio
            // (8.8:1, generic-turbocharged) applies instead of a guessed figure.
        },

        new EngineEntry
        {
            Name = "BMW M50B25",
            Manufacturer = "BMW",
            Code = "M50B25",
            Family = "M50",
            BoreMm = 84.0,
            StrokeMm = 75.0,
            CompressionRatio = 10.0,
            CylinderCount = 6,
            ValveCountPerCylinder = 4,
            Aspiration = EngineAspiration.NaturallyAspirated,
            DisplacementCc = 2494.0,
            PeakPowerRpm = 6000.0,
            Source = "Wikipedia, \"BMW M50\", https://en.wikipedia.org/wiki/BMW_M50",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, compression ratio, valve count and "
                + "peak-power rpm are bare numeric fact taken from the article's infobox, not copied prose.",
            Tags = ["BMW", "M50", "straight-six", "naturally-aspirated", "E36", "tuner-popular"],

            // The single-VANOS M50B25TU (1992-1996) raised CR to 10.5:1 with the same
            // bore/stroke/power figure and a different torque peak; the 1990-1992
            // M50B25 figure above is used since it is the base, unambiguous variant.
        },

        new EngineEntry
        {
            Name = "BMW N54B30",
            Manufacturer = "BMW",
            Code = "N54B30",
            Family = "N54",
            BoreMm = 84.0,
            StrokeMm = 89.6,
            CompressionRatio = 10.2,
            CylinderCount = 6,
            Aspiration = EngineAspiration.Turbocharged,
            DisplacementCc = 2979.0,
            PeakPowerRpm = 5800.0,
            Source = "Wikipedia, \"BMW N54\", https://en.wikipedia.org/wiki/BMW_N54",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, compression ratio and peak-power "
                + "rpm are bare numeric fact taken from the article, not copied prose.",
            Tags = ["BMW", "N54", "straight-six", "twin-turbo", "335i", "tuner-popular"],

            // No valve count cited: the article states DOHC with VVT but does not
            // give a numeric valves-per-cylinder figure in the fetched text.
            // The 302 bhp @ 5,800 rpm initial rating is the article's own figure;
            // it separately notes independent dynos suggest the true output is
            // higher, which is not reflected here since it is not the cited number.
        },

        new EngineEntry
        {
            Name = "BMW B58B30M0",
            Manufacturer = "BMW",
            Code = "B58B30M0",
            Family = "B58",
            BoreMm = 82.0,
            StrokeMm = 94.6,
            CompressionRatio = 11.0,
            CylinderCount = 6,
            ValveCountPerCylinder = 4,
            Aspiration = EngineAspiration.Turbocharged,
            DisplacementCc = 2998.0,
            Source = "Wikipedia, \"BMW B58\", https://en.wikipedia.org/wiki/BMW_B58",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, compression ratio and valve count "
                + "are bare numeric fact taken from the article, not copied prose.",
            Tags = ["BMW", "B58", "straight-six", "turbocharged", "tuner-popular"],

            // No peak-power rpm cited: the article lists many state-of-tune variants
            // (290-360 PS standard tunes, up to 441 PS in the highest one) without a
            // single figure representing "the" B58 the way a base-model spec would.
            // EngineEntry.Seed()'s FallbackTargetRpm applies instead.
        },

        new EngineEntry
        {
            Name = "BMW M54B30",
            Manufacturer = "BMW",
            Code = "M54B30",
            Family = "M54",
            BoreMm = 84.0,
            StrokeMm = 89.6,
            CompressionRatio = 10.2,
            CylinderCount = 6,
            Aspiration = EngineAspiration.NaturallyAspirated,
            DisplacementCc = 2979.0,
            PeakPowerRpm = 5900.0,
            Source = "Wikipedia, \"BMW M54\", https://en.wikipedia.org/wiki/BMW_M54",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, compression ratio and peak-power "
                + "rpm are bare numeric fact taken from the article, not copied prose.",
            Tags = ["BMW", "M54", "straight-six", "naturally-aspirated", "E46", "tuner-popular"],

            // No valve count cited: the article gives DOHC with double-VANOS but no
            // numeric valves-per-cylinder figure in the fetched text.
        },

        new EngineEntry
        {
            Name = "Volkswagen/Audi EA113 1.8T (AMU/BEA)",
            Manufacturer = "Volkswagen/Audi",
            Family = "EA113",
            BoreMm = 81.0,
            StrokeMm = 86.4,
            CylinderCount = 4,
            ValveCountPerCylinder = 5,
            Aspiration = EngineAspiration.Turbocharged,
            DisplacementCc = 1781.0,
            PeakPowerRpm = 5900.0,
            Source = "Wikipedia, \"List of Volkswagen Group petrol engines\", "
                + "https://en.wikipedia.org/wiki/List_of_Volkswagen_Group_petrol_engines",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, valve count and peak-power rpm are "
                + "bare numeric fact taken from the article's engine table, not copied prose.",
            Tags = ["Volkswagen", "Audi", "EA113", "1.8T", "inline-four", "turbocharged", "tuner-popular"],

            // No compression ratio cited: the article gives 9.0-9.5:1 as a range for
            // the whole 1.8T family, not specific to the 225 PS AMU/BEA state of
            // tune picked here. Power/rpm (225 PS @ 5,900 rpm) IS variant-specific.
        },

        new EngineEntry
        {
            Name = "Audi/Volkswagen 2.7T biturbo V6",
            Manufacturer = "Audi/Volkswagen",
            Family = "2.7T",
            BoreMm = 81.0,
            StrokeMm = 86.4,
            CylinderCount = 6,
            ValveCountPerCylinder = 5,
            Aspiration = EngineAspiration.Turbocharged,
            DisplacementCc = 2671.0,
            PeakPowerRpm = 5800.0,
            Source = "Wikipedia, \"List of discontinued Volkswagen Group petrol engines\", "
                + "https://en.wikipedia.org/wiki/List_of_discontinued_Volkswagen_Group_petrol_engines "
                + "(power/rpm corroborated on \"Audi S4\", https://en.wikipedia.org/wiki/Audi_S4)",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, valve count and peak-power rpm are "
                + "bare numeric fact taken from the articles, not copied prose.",
            Tags = ["Audi", "Volkswagen", "V6", "biturbo", "B5 S4", "tuner-popular"],

            // No compression ratio cited: the article gives 9.0-9.9:1 as a range for
            // the whole 2.7T family, not specific to the B5 S4 EU-spec figure (195 kW
            // / 265 PS @ 5,800 rpm) picked here.
        },

        new EngineEntry
        {
            Name = "Volkswagen VR6 2.8 (AAA)",
            Manufacturer = "Volkswagen",
            Code = "AAA",
            Family = "VR6",
            BoreMm = 81.0,
            StrokeMm = 90.3,
            CylinderCount = 6,
            ValveCountPerCylinder = 2,
            Aspiration = EngineAspiration.NaturallyAspirated,
            DisplacementCc = 2792.0,
            Source = "Wikipedia, \"VR6 engine\", https://en.wikipedia.org/wiki/VR6_engine",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement and valve count are bare numeric "
                + "fact taken from the article, not copied prose.",
            Tags = ["Volkswagen", "VR6", "V6", "narrow-angle", "naturally-aspirated", "Corrado", "tuner-popular"],

            // No compression ratio cited: the VR6 engine article gives 10:1, but
            // Wikipedia's own "List of discontinued Volkswagen Group petrol engines"
            // gives 10.5:1 for the same AAA code — the two articles disagree, so
            // neither is stamped rather than silently picking one.
            // No peak-power rpm cited: neither article states one for this variant.
        },

        new EngineEntry
        {
            Name = "Audi 4.2 V8 FSI",
            Manufacturer = "Audi",
            Family = "Volkswagen-Audi V8",
            BoreMm = 84.5,
            StrokeMm = 92.8,
            CompressionRatio = 12.5,
            CylinderCount = 8,
            ValveCountPerCylinder = 4,
            Aspiration = EngineAspiration.NaturallyAspirated,
            DisplacementCc = 4163.0,
            PeakPowerRpm = 7800.0,
            Source = "Wikipedia, \"Volkswagen-Audi V8 engine\", "
                + "https://en.wikipedia.org/wiki/Volkswagen-Audi_V8_engine "
                + "(power/rpm corroborated on \"Audi RS 4\", https://en.wikipedia.org/wiki/Audi_RS_4)",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, compression ratio, valve count and "
                + "peak-power rpm are bare numeric fact taken from the articles, not copied prose.",
            Tags = ["Audi", "V8", "FSI", "naturally-aspirated", "RS4", "R8", "tuner-popular"],
        },

        new EngineEntry
        {
            Name = "GM LS1",
            Manufacturer = "GM/Chevrolet",
            Code = "LS1",
            Family = "GM LS-based small-block",
            BoreMm = 99.0,
            StrokeMm = 92.0,
            CompressionRatio = 10.25,
            CylinderCount = 8,
            ValveCountPerCylinder = 2,
            Aspiration = EngineAspiration.NaturallyAspirated,
            DisplacementCc = 5665.0,
            PeakPowerRpm = 5600.0,
            Source = "Wikipedia, \"General Motors LS-based small-block engine\", "
                + "https://en.wikipedia.org/wiki/General_Motors_LS-based_small-block_engine",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, compression ratio, valve count and "
                + "peak-power rpm are bare numeric fact taken from the article's engine table, not copied prose.",
            Tags = ["GM", "Chevrolet", "LS1", "V8", "OHV", "naturally-aspirated", "Corvette", "tuner-popular"],
        },

        new EngineEntry
        {
            Name = "GM LS3",
            Manufacturer = "GM/Chevrolet",
            Code = "LS3",
            Family = "GM LS-based small-block",
            BoreMm = 103.25,
            StrokeMm = 92.0,
            CompressionRatio = 10.7,
            CylinderCount = 8,
            ValveCountPerCylinder = 2,
            Aspiration = EngineAspiration.NaturallyAspirated,
            DisplacementCc = 6162.0,
            PeakPowerRpm = 5900.0,
            Source = "Wikipedia, \"General Motors LS-based small-block engine\", "
                + "https://en.wikipedia.org/wiki/General_Motors_LS-based_small-block_engine",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, compression ratio, valve count and "
                + "peak-power rpm are bare numeric fact taken from the article's engine table, not copied prose.",
            Tags = ["GM", "Chevrolet", "LS3", "V8", "OHV", "naturally-aspirated", "Corvette", "Camaro",
                "tuner-popular"],
        },

        new EngineEntry
        {
            Name = "GM LS7",
            Manufacturer = "GM/Chevrolet",
            Code = "LS7",
            Family = "GM LS-based small-block",
            BoreMm = 104.8,
            StrokeMm = 101.6,
            CylinderCount = 8,
            ValveCountPerCylinder = 2,
            Aspiration = EngineAspiration.NaturallyAspirated,
            DisplacementCc = 7011.0,
            PeakPowerRpm = 6300.0,
            Source = "Wikipedia, \"General Motors LS-based small-block engine\", "
                + "https://en.wikipedia.org/wiki/General_Motors_LS-based_small-block_engine",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, valve count and peak-power rpm are "
                + "bare numeric fact taken from the article's engine table, not copied prose.",
            Tags = ["GM", "Chevrolet", "LS7", "V8", "OHV", "naturally-aspirated", "Corvette Z06", "tuner-popular"],

            // No compression ratio cited: not stated on the page.
        },

        new EngineEntry
        {
            Name = "GM LS9",
            Manufacturer = "GM/Chevrolet",
            Code = "LS9",
            Family = "GM LS-based small-block",
            BoreMm = 103.25,
            StrokeMm = 92.0,
            CompressionRatio = 9.1,
            CylinderCount = 8,
            ValveCountPerCylinder = 2,
            Aspiration = EngineAspiration.Supercharged,
            DisplacementCc = 6162.0,
            PeakPowerRpm = 6500.0,
            Source = "Wikipedia, \"General Motors LS-based small-block engine\", "
                + "https://en.wikipedia.org/wiki/General_Motors_LS-based_small-block_engine",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, compression ratio, valve count and "
                + "peak-power rpm are bare numeric fact taken from the article's engine table, not copied prose.",
            Tags = ["GM", "Chevrolet", "LS9", "V8", "OHV", "supercharged", "Corvette ZR1", "tuner-popular"],
        },

        new EngineEntry
        {
            Name = "Honda K20A",
            Manufacturer = "Honda",
            Code = "K20A",
            Family = "Honda K engine",
            BoreMm = 86.0,
            StrokeMm = 86.0,
            CompressionRatio = 11.5,
            CylinderCount = 4,
            ValveCountPerCylinder = 4,
            Aspiration = EngineAspiration.NaturallyAspirated,
            DisplacementCc = 1998.0,
            PeakPowerRpm = 8000.0,
            Source = "Wikipedia, \"Honda K engine\", https://en.wikipedia.org/wiki/Honda_K_engine",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, compression ratio, valve count and "
                + "peak-power rpm are bare numeric fact taken from the article, not copied prose.",
            Tags = ["Honda", "K20A", "inline-four", "naturally-aspirated", "VTEC", "Civic Type R",
                "tuner-popular"],

            // Figures are for the 2001-2006 JDM Civic Type R (EP3) state of tune,
            // 212 hp @ 8,000 rpm. The K20A1-A9 family spans 9.7:1-11.7:1 CR across
            // markets/years per the article; this is not "the" K20A generically.
        },

        new EngineEntry
        {
            Name = "Honda B18C",
            Manufacturer = "Honda",
            Code = "B18C",
            Family = "Honda B engine",
            BoreMm = 81.0,
            StrokeMm = 87.2,
            CompressionRatio = 11.1,
            CylinderCount = 4,
            ValveCountPerCylinder = 4,
            Aspiration = EngineAspiration.NaturallyAspirated,
            DisplacementCc = 1797.0,
            PeakPowerRpm = 8000.0,
            Source = "Wikipedia, \"Honda B engine\", https://en.wikipedia.org/wiki/Honda_B_engine",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, compression ratio, valve count and "
                + "peak-power rpm are bare numeric fact taken from the article, not copied prose.",
            Tags = ["Honda", "B18C", "inline-four", "naturally-aspirated", "VTEC", "Integra Type R",
                "tuner-popular"],

            // Figures are for the JDM Integra Type R (DC2/DB8), 1995-2000 "96/98
            // spec", 197 hp @ 8,000 rpm.
        },

        new EngineEntry
        {
            Name = "Honda F20C",
            Manufacturer = "Honda",
            Code = "F20C",
            Family = "Honda F20C engine",
            BoreMm = 87.0,
            StrokeMm = 84.0,
            CompressionRatio = 11.7,
            CylinderCount = 4,
            ValveCountPerCylinder = 4,
            Aspiration = EngineAspiration.NaturallyAspirated,
            DisplacementCc = 1997.0,
            PeakPowerRpm = 8300.0,
            Source = "Wikipedia, \"Honda F20C engine\", https://en.wikipedia.org/wiki/Honda_F20C_engine",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, compression ratio, valve count and "
                + "peak-power rpm are bare numeric fact taken from the article, not copied prose.",
            Tags = ["Honda", "F20C", "inline-four", "naturally-aspirated", "VTEC", "S2000", "high-revving",
                "tuner-popular"],

            // JDM spec: 11.7:1 CR, 250 PS @ 8,300 rpm. The article separately gives
            // 11.0:1 CR / 240 hp for the North American/European market spec.
        },

        new EngineEntry
        {
            Name = "Honda J35A8",
            Manufacturer = "Honda",
            Code = "J35A8",
            Family = "Honda J engine",
            BoreMm = 89.0,
            StrokeMm = 93.0,
            CompressionRatio = 11.0,
            CylinderCount = 6,
            ValveCountPerCylinder = 4,
            Aspiration = EngineAspiration.NaturallyAspirated,
            DisplacementCc = 3471.0,
            PeakPowerRpm = 6200.0,
            Source = "Wikipedia, \"Honda J engine\", https://en.wikipedia.org/wiki/Honda_J_engine",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, compression ratio, valve count and "
                + "peak-power rpm are bare numeric fact taken from the article, not copied prose.",
            Tags = ["Honda", "J35", "V6", "naturally-aspirated", "SOHC VTEC", "Acura RL"],

            // Honda Legend KB1 / Acura RL tune, 286-290 hp @ 6,200 rpm. SOHC with
            // 24 valves total (4/cylinder) per the article, not DOHC.
        },

        new EngineEntry
        {
            Name = "Toyota 2JZ-GE",
            Manufacturer = "Toyota",
            Code = "2JZ-GE",
            Family = "JZ",
            BoreMm = 86.0,
            StrokeMm = 86.0,
            CylinderCount = 6,
            ValveCountPerCylinder = 4,
            Aspiration = EngineAspiration.NaturallyAspirated,
            DisplacementCc = 2997.0,
            PeakPowerRpm = 6000.0,
            Source = "Wikipedia, \"Toyota JZ engine\", https://en.wikipedia.org/wiki/Toyota_JZ_engine",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement and valve count are bare numeric "
                + "fact taken from the article, not copied prose.",
            Tags = ["Toyota", "2JZ-GE", "straight-six", "naturally-aspirated", "Supra", "tuner-popular"],

            // No compression ratio cited: not stated on the page. Peak power is
            // given as a 215-230 PS range across model years at 5,800-6,000 rpm;
            // 6,000 rpm (the upper, cited endpoint) is used as the tuning target.
        },

        new EngineEntry
        {
            Name = "Toyota 4A-GE (16V Red Top)",
            Manufacturer = "Toyota",
            Code = "4A-GE",
            Family = "Toyota A engine",
            BoreMm = 81.0,
            StrokeMm = 77.0,
            CompressionRatio = 10.3,
            CylinderCount = 4,
            ValveCountPerCylinder = 4,
            Aspiration = EngineAspiration.NaturallyAspirated,
            DisplacementCc = 1587.0,
            PeakPowerRpm = 7200.0,
            Source = "Wikipedia, \"Toyota A engine\", https://en.wikipedia.org/wiki/Toyota_A_engine",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, compression ratio, valve count and "
                + "peak-power rpm are bare numeric fact taken from the article, not copied prose.",
            Tags = ["Toyota", "4A-GE", "inline-four", "naturally-aspirated", "AE86", "high-revving",
                "tuner-popular"],

            // Third-generation 16-valve "Red Top" (June 1989-June 1991), the final
            // 16V AE86-era version; the article covers several earlier 16V
            // generations with different compression/power. 140 PS @ 7,200 rpm
            // (Japan/export spec); North American spec is 130 hp @ 6,800 rpm.
        },

        new EngineEntry
        {
            Name = "Toyota 3S-GTE (Gen 4)",
            Manufacturer = "Toyota",
            Code = "3S-GTE",
            Family = "Toyota S engine",
            BoreMm = 86.0,
            StrokeMm = 86.0,
            CompressionRatio = 9.0,
            CylinderCount = 4,
            ValveCountPerCylinder = 4,
            Aspiration = EngineAspiration.Turbocharged,
            DisplacementCc = 1998.0,
            PeakPowerRpm = 6200.0,
            Source = "Wikipedia, \"Toyota S engine\", https://en.wikipedia.org/wiki/Toyota_S_engine",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, compression ratio, valve count and "
                + "peak-power rpm are bare numeric fact taken from the article, not copied prose.",
            Tags = ["Toyota", "3S-GTE", "inline-four", "turbocharged", "Celica GT-Four", "MR2 Turbo",
                "tuner-popular"],

            // Fourth-generation figure (260 PS @ 6,200 rpm, 9.0:1 CR); the article
            // gives earlier generations from 8.5:1 CR / 185 PS.
        },

        new EngineEntry
        {
            Name = "Nissan SR20DET",
            Manufacturer = "Nissan",
            Code = "SR20DET",
            Family = "Nissan SR engine",
            BoreMm = 86.0,
            StrokeMm = 86.0,
            CylinderCount = 4,
            ValveCountPerCylinder = 4,
            Aspiration = EngineAspiration.Turbocharged,
            DisplacementCc = 1998.0,
            PeakPowerRpm = 6000.0,
            Source = "Wikipedia, \"Nissan SR engine\", https://en.wikipedia.org/wiki/Nissan_SR_engine",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement and valve count are bare numeric "
                + "fact taken from the article, not copied prose.",
            Tags = ["Nissan", "SR20DET", "inline-four", "turbocharged", "Silvia", "180SX", "tuner-popular"],

            // No compression ratio cited: the article gives 10.0:1 for the naturally
            // aspirated SR20DE nearby, but does not confirm the DET shares it. The
            // article does not break specs out by chassis (S13/S14/S15); peak power
            // ranges 201-247 hp @ 6,000-6,400 rpm across variants/years, and 6,000
            // rpm (the lower, cited endpoint) is used as the tuning target.
        },

        new EngineEntry
        {
            Name = "Nissan RB26DETT",
            Manufacturer = "Nissan",
            Code = "RB26DETT",
            Family = "Nissan RB engine",
            BoreMm = 86.0,
            StrokeMm = 73.7,
            CompressionRatio = 9.0,
            CylinderCount = 6,
            ValveCountPerCylinder = 4,
            Aspiration = EngineAspiration.Turbocharged,
            DisplacementCc = 2568.0,
            PeakPowerRpm = 6800.0,
            Source = "Wikipedia, \"Nissan RB engine\", https://en.wikipedia.org/wiki/Nissan_RB_engine",
            Licence = "CC BY-SA 4.0 (Wikipedia). Bore, stroke, displacement, compression ratio, valve count and "
                + "peak-power rpm are bare numeric fact taken from the article, not copied prose.",
            Tags = ["Nissan", "RB26DETT", "straight-six", "twin-turbo", "Skyline GT-R", "tuner-popular"],

            // 1989-2002 (R32/R33/R34 GT-R), officially rated 276 bhp/280 PS @ 6,800
            // rpm under Japan's "Gentlemen's Agreement" cap; the article notes later
            // factory output was actually higher (320 PS/316 hp), which is not used
            // here since 280 PS is the article's own cited, published figure.
        },
    ];

    /// <summary>A fresh database seeded with every curated entry.</summary>
    public static EngineDatabase Default()
    {
        var database = new EngineDatabase();
        foreach (var entry in Curated)
        {
            database.Add(entry);
        }

        return database;
    }
}
