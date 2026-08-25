namespace WaveBench.Core.EngineModel;

/// <summary>
/// Exact slider-crank kinematics with rod ratio and wrist-pin offset
/// (plan §2.5). Crank angle convention: θ = 0 at TDC firing, degrees.
/// </summary>
public sealed record CrankGeometry
{
    public required double Bore { get; init; }

    public required double Stroke { get; init; }

    public required double RodLength { get; init; }

    public double PinOffset { get; init; }

    public required double CompressionRatio { get; init; }

    public double CrankRadius => Stroke / 2.0;

    public double PistonArea => Math.PI / 4.0 * Bore * Bore;

    /// <summary>Displacement of one cylinder, m³ (exact, including pin offset).</summary>
    public double DisplacedVolume => PistonArea * (PistonPosition(180.0) - PistonPosition(0.0));

    public double ClearanceVolume => DisplacedVolume / (CompressionRatio - 1.0);

    /// <summary>
    /// Piston distance from the TDC position, m (0 at TDC), exact:
    /// x(θ) = x_TDC − [a·cosθ + √(l² − (a·sinθ − e)²)].
    /// </summary>
    public double PistonPosition(double crankAngleDeg)
    {
        var a = CrankRadius;
        var l = RodLength;
        var e = PinOffset;
        var theta = crankAngleDeg * Math.PI / 180.0;

        // With pin offset, true TDC is where the extension is maximal.
        var xTdc = Math.Sqrt((l + a) * (l + a) - e * e);
        var s = a * Math.Sin(theta) - e;
        return xTdc - (a * Math.Cos(theta) + Math.Sqrt(l * l - s * s));
    }

    /// <summary>Cylinder volume at crank angle, m³.</summary>
    public double Volume(double crankAngleDeg) =>
        ClearanceVolume + PistonArea * PistonPosition(crankAngleDeg);

    /// <summary>dV/dθ, m³ per crank radian (central difference; exact enough for the energy equation).</summary>
    public double VolumeDerivative(double crankAngleDeg)
    {
        const double h = 1e-3; // degrees
        return (Volume(crankAngleDeg + h) - Volume(crankAngleDeg - h))
               / (2.0 * h * Math.PI / 180.0);
    }

    /// <summary>Mean piston speed at engine speed N (rpm), m/s — the FSAE noise-test basis.</summary>
    public double MeanPistonSpeed(double rpm) => 2.0 * Stroke * rpm / 60.0;

    /// <summary>
    /// Crank angle after TDC of maximum piston speed (≈ 76° for typical rod
    /// ratios; exact via the kinematics), deg.
    /// </summary>
    public double MaxPistonSpeedAngle()
    {
        var best = 0.0;
        var bestSpeed = 0.0;
        for (var theta = 30.0; theta <= 150.0; theta += 0.1)
        {
            var speed = Math.Abs(PistonPosition(theta + 0.05) - PistonPosition(theta - 0.05)) / 0.1;
            if (speed > bestSpeed)
            {
                bestSpeed = speed;
                best = theta;
            }
        }

        return best;
    }
}
