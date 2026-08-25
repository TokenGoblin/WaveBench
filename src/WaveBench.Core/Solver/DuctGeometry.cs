namespace WaveBench.Core.Solver;

/// <summary>
/// Discrete geometry of a quasi-1D duct on a uniform axial mesh: face areas
/// A_{i+1/2}, cell areas (face average, so the discrete p·dA/dx source
/// telescopes exactly — well-balancedness, plan §5.1), hydraulic diameters
/// and wall roughness.
/// </summary>
public sealed class DuctGeometry
{
    private DuctGeometry(int cellCount, double cellSize, double[] faceArea, double roughness)
    {
        CellCount = cellCount;
        CellSize = cellSize;
        FaceArea = faceArea;
        Roughness = roughness;

        CellArea = new double[cellCount];
        HydraulicDiameter = new double[cellCount];
        for (var i = 0; i < cellCount; i++)
        {
            CellArea[i] = 0.5 * (faceArea[i] + faceArea[i + 1]);
            HydraulicDiameter[i] = Math.Sqrt(4.0 * CellArea[i] / Math.PI);
        }
    }

    public int CellCount { get; }

    public double CellSize { get; }

    public double Length => CellCount * CellSize;

    /// <summary>A_{i+1/2}, length CellCount + 1.</summary>
    public double[] FaceArea { get; }

    public double[] CellArea { get; }

    /// <summary>Circular-equivalent hydraulic diameter per cell.</summary>
    public double[] HydraulicDiameter { get; }

    /// <summary>Wall roughness ε, m (per-pipe property, plan §2.1).</summary>
    public double Roughness { get; }

    public static DuctGeometry Uniform(double length, int cellCount, double diameter, double roughness = 0.0)
    {
        var area = Math.PI / 4.0 * diameter * diameter;
        var faces = new double[cellCount + 1];
        Array.Fill(faces, area);
        return new DuctGeometry(cellCount, length / cellCount, faces, roughness);
    }

    /// <summary>Linear diameter taper from dLeft to dRight.</summary>
    public static DuctGeometry Taper(
        double length, int cellCount, double leftDiameter, double rightDiameter, double roughness = 0.0)
    {
        var faces = new double[cellCount + 1];
        for (var j = 0; j <= cellCount; j++)
        {
            var d = leftDiameter + (rightDiameter - leftDiameter) * j / cellCount;
            faces[j] = Math.PI / 4.0 * d * d;
        }

        return new DuctGeometry(cellCount, length / cellCount, faces, roughness);
    }

    /// <summary>Arbitrary diameter profile d(x), sampled at faces.</summary>
    public static DuctGeometry FromDiameterProfile(
        double length, int cellCount, Func<double, double> diameterAt, double roughness = 0.0)
    {
        var faces = new double[cellCount + 1];
        var dx = length / cellCount;
        for (var j = 0; j <= cellCount; j++)
        {
            var d = diameterAt(j * dx);
            faces[j] = Math.PI / 4.0 * d * d;
        }

        return new DuctGeometry(cellCount, dx, faces, roughness);
    }
}
