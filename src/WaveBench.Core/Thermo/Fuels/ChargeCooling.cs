namespace WaveBench.Core.Thermo.Fuels;

public enum InjectorLocation
{
    ThrottleBody,
    Port,
    Direct,
}

/// <summary>
/// Evaporative charge cooling (plan §2.4):
///   ΔT = x_evap · ṁ_fuel · Δh_vap / (ṁ_air·c_p,air + ṁ_fuel,vap·c_p,fuel)
/// Returned as a positive temperature drop. x_evap is the fraction evaporated
/// upstream of the valve; the shipped defaults are typical values (documented
/// as empirical, user-adjustable): throttle-body 0.40 (long, hot path),
/// port 0.22 (typical 20-30% pre-valve evaporation for port injection),
/// direct 0.05 (essentially all evaporation happens in-cylinder).
/// </summary>
public static class ChargeCooling
{
    // Cached standard-air mixture per database: the Fuel-record overload is
    // called inside cycle iteration and must not rebuild air each time.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SpeciesDatabase, MixtureThermo>
        AirCache = new();

    /// <summary>
    /// WaveBench engineering defaults for the pre-valve evaporated fraction —
    /// not a literature correlation. They represent the commonly reported
    /// 20–30% pre-valve evaporation for port fuel injection of gasoline and
    /// alcohols (throttle-body higher via the longer, hotter path; direct
    /// injection evaporates essentially entirely in-cylinder). Treat as
    /// calibration parameters; the UI must present them as adjustable and
    /// label them empirical (plan §2.4).
    /// </summary>
    public static double DefaultEvaporatedFraction(InjectorLocation location) => location switch
    {
        InjectorLocation.ThrottleBody => 0.40,
        InjectorLocation.Port => 0.22,
        InjectorLocation.Direct => 0.05,
        _ => throw new ArgumentOutOfRangeException(nameof(location)),
    };

    /// <summary>
    /// Temperature drop (K) of the charge from fuel evaporation, per unit fuel:
    /// air mass flow expressed via the actual AFR (= stoich AFR · λ).
    /// </summary>
    public static double TemperatureDrop(
        double evaporatedFraction,
        double airFuelRatio,
        double latentHeat,
        double airCp,
        double fuelVapourCp)
    {
        if (evaporatedFraction is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(evaporatedFraction));
        }

        var denominator = airFuelRatio * airCp + evaporatedFraction * fuelVapourCp;
        return evaporatedFraction * latentHeat / denominator;
    }

    /// <summary>
    /// Convenience overload using the fuel record, thermo database c_p values
    /// at the given charge temperature, and λ.
    /// </summary>
    public static double TemperatureDrop(
        Fuel fuel,
        double lambda,
        InjectorLocation location,
        SpeciesDatabase database,
        double chargeTemperature = PhysicalConstants.ReferenceTemperature)
    {
        var air = AirCache.GetValue(database, db => new MixtureThermo(GasComposition.DryAir(db), db));
        var vapourCp = database[fuel.VapourSpeciesName].Cp(chargeTemperature);
        return TemperatureDrop(
            DefaultEvaporatedFraction(location),
            Stoichiometry.AirFuelRatio(fuel.Formula, lambda),
            fuel.LatentHeatOfVaporisation,
            air.Cp(chargeTemperature),
            vapourCp);
    }
}
