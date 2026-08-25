using WaveBench.Model.Units;

namespace WaveBench.Core.Thermo.Fuels;

public sealed record KnockIntegralResult(
    double Integral,
    bool KnockPredicted,
    double? KnockTimeSeconds);

/// <summary>
/// Autoignition induction time by Douaud &amp; Eyzat (SAE 780080):
///   τ = 17.68 · (ON/100)^3.402 · p^(−1.7) · exp(3800/T)   [τ ms, p atm, T K]
/// integrated by the Livengood–Wu criterion ∫ dt/τ = 1 (plan §2.4).
/// Validity: gasoline-type fuels, roughly ON 80–110; do not apply to hydrogen
/// or gaseous fuels far outside the gasoline family.
/// </summary>
public static class KnockModel
{
    /// <summary>Induction time, seconds. Pressure in Pa, temperature in K.</summary>
    public static double InductionTime(double octaneNumber, double pressure, double temperature)
    {
        if (octaneNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(octaneNumber));
        }

        return OctaneFactor(octaneNumber) * PressureTemperatureFactor(pressure, temperature);
    }

    /// <summary>Unit-safe overload: the correlation's native atm/K are handled internally.</summary>
    public static double InductionTime(double octaneNumber, Pressure pressure, Temperature temperature) =>
        InductionTime(octaneNumber, pressure.Pascals, temperature.Kelvin);

    /// <summary>17.68·(ON/100)^3.402 in seconds — loop-invariant part of τ.</summary>
    private static double OctaneFactor(double octaneNumber) =>
        17.68e-3 * Math.Pow(octaneNumber / 100.0, 3.402);

    private static double PressureTemperatureFactor(double pressure, double temperature) =>
        Math.Pow(pressure / 101_325.0, -1.7) * Math.Exp(3800.0 / temperature);

    /// <summary>
    /// Livengood–Wu integral over an unburned-zone (time, pressure, temperature)
    /// trace, trapezoidal rule. Knock is predicted where the integral reaches 1;
    /// the crossing time is linearly interpolated.
    /// </summary>
    public static KnockIntegralResult LivengoodWu(
        IReadOnlyList<(double Time, double Pressure, double Temperature)> trace,
        double octaneNumber)
    {
        if (trace.Count < 2)
        {
            throw new ArgumentException("Need at least two trace points.", nameof(trace));
        }

        if (octaneNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(octaneNumber));
        }

        var octaneFactor = OctaneFactor(octaneNumber); // loop-invariant
        var integral = 0.0;
        var previousRate = 1.0 / (octaneFactor * PressureTemperatureFactor(trace[0].Pressure, trace[0].Temperature));

        for (var i = 1; i < trace.Count; i++)
        {
            var rate = 1.0 / (octaneFactor * PressureTemperatureFactor(trace[i].Pressure, trace[i].Temperature));
            var dt = trace[i].Time - trace[i - 1].Time;
            if (dt < 0)
            {
                throw new ArgumentException("Trace times must be non-decreasing.", nameof(trace));
            }

            var step = 0.5 * (previousRate + rate) * dt;
            if (integral + step >= 1.0)
            {
                var fraction = (1.0 - integral) / step;
                var knockTime = trace[i - 1].Time + fraction * dt;
                return new KnockIntegralResult(1.0, true, knockTime);
            }

            integral += step;
            previousRate = rate;
        }

        return new KnockIntegralResult(integral, false, null);
    }
}
