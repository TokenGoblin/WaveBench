namespace WaveBench.Optimize;

/// <summary>
/// Space-filling designs of experiment (plan §9.4): Sobol and Latin hypercube.
///
/// <b>Why not just random.</b> A uniform random sample of 32 points in six
/// dimensions leaves clumps and holes; a hole is a region the response surface
/// is fitted through with no data, and a clump is evaluations spent twice on
/// the same information. Both of these fill the cube evenly by construction,
/// which is worth a great deal when each point costs a converged sweep.
/// </summary>
public static class Doe
{
    /// <summary>
    /// A Sobol low-discrepancy sequence over the unit cube.
    ///
    /// Uses direction numbers from primitive polynomials with all-ones initial
    /// values (the standard construction; Bratley &amp; Fox, ACM TOMS 14(1),
    /// 1988, Algorithm 659). The generator is a Gray-code recurrence, so each
    /// point costs one XOR per dimension.
    ///
    /// <b>Deterministic by design.</b> Plan Part 0 requires that the same
    /// input produce bit-identical results; a DOE seeded from the clock would
    /// make every optimisation run unreproducible, and an unreproducible
    /// optimisation cannot be defended in a report.
    /// </summary>
    /// <param name="dimension">Number of variables; up to <see cref="MaxDimension"/>.</param>
    /// <param name="count">How many points.</param>
    /// <param name="skip">
    /// Points to discard from the front of the sequence. The first Sobol point
    /// is the origin and the early ones are strongly correlated across
    /// dimensions; skipping a power of two is the usual remedy.
    /// </param>
    public static IReadOnlyList<double[]> Sobol(int dimension, int count, int skip = 1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(skip);

        if (dimension > MaxDimension)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dimension),
                $"This generator carries direction numbers for {MaxDimension} dimensions. Beyond that, use a "
                + "Latin hypercube — a Sobol sequence with made-up direction numbers is not low-discrepancy, "
                + "it just looks like one.");
        }

        const int bits = 30;
        var direction = BuildDirectionNumbers(dimension, bits);
        var current = new uint[dimension];
        var points = new List<double[]>(count);

        var total = count + skip;
        for (var i = 1; i <= total; i++)
        {
            // Gray-code: point i differs from point i-1 in exactly the
            // rightmost-zero-bit column.
            var c = 0;
            var value = (uint)(i - 1);
            while ((value & 1) == 1)
            {
                value >>= 1;
                c++;
            }

            for (var d = 0; d < dimension; d++)
            {
                current[d] ^= direction[d][c];
            }

            if (i > skip)
            {
                var point = new double[dimension];
                for (var d = 0; d < dimension; d++)
                {
                    point[d] = current[d] / (double)(1u << bits);
                }

                points.Add(point);
            }
        }

        return points;
    }

    /// <summary>
    /// The highest dimension the shipped direction numbers cover. Six
    /// primitive polynomials past the first dimension, which reaches the
    /// variable counts a manifold or cam optimisation actually uses.
    /// </summary>
    public const int MaxDimension = 7;

    private static uint[][] BuildDirectionNumbers(int dimension, int bits)
    {
        // (degree, polynomial coefficients as a bit field, initial m values).
        // The first dimension is the van der Corput sequence and has no
        // polynomial; the rest are the standard primitive polynomials in
        // Bratley & Fox's ordering.
        (int Degree, uint Polynomial, uint[] Initial)[] parameters =
        [
            (0, 0, []),
            (1, 0, [1]),
            (2, 1, [1, 3]),
            (3, 1, [1, 3, 1]),
            (3, 2, [1, 1, 1]),
            (4, 1, [1, 1, 3, 3]),
            (4, 4, [1, 3, 5, 13]),
        ];

        var direction = new uint[dimension][];

        for (var d = 0; d < dimension; d++)
        {
            direction[d] = new uint[bits];

            if (d == 0)
            {
                // v_k = 2^(bits-1-k): the plain radical-inverse sequence.
                for (var k = 0; k < bits; k++)
                {
                    direction[0][k] = 1u << (bits - 1 - k);
                }

                continue;
            }

            var (degree, polynomial, initial) = parameters[d];

            for (var k = 0; k < degree; k++)
            {
                direction[d][k] = initial[k] << (bits - 1 - k);
            }

            for (var k = degree; k < bits; k++)
            {
                var v = direction[d][k - degree] ^ (direction[d][k - degree] >> degree);

                for (var j = 1; j < degree; j++)
                {
                    if (((polynomial >> (degree - 1 - j)) & 1) == 1)
                    {
                        v ^= direction[d][k - j];
                    }
                }

                direction[d][k] = v;
            }
        }

        return direction;
    }

    /// <summary>
    /// A Latin hypercube: each dimension is split into <paramref name="count"/>
    /// equal strata and every stratum is used exactly once.
    ///
    /// Weaker than Sobol at filling the cube in the corners, but it guarantees
    /// one-dimensional coverage exactly, which is what a screening study
    /// actually wants — and unlike Sobol it works in any dimension.
    ///
    /// Deterministic given <paramref name="seed"/>.
    /// </summary>
    public static IReadOnlyList<double[]> LatinHypercube(int dimension, int count, int seed = 20260913)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dimension);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var random = new Random(seed);
        var points = new double[count][];
        for (var i = 0; i < count; i++)
        {
            points[i] = new double[dimension];
        }

        for (var d = 0; d < dimension; d++)
        {
            var order = Enumerable.Range(0, count).ToArray();

            // Fisher-Yates, so each stratum lands in exactly one point.
            for (var i = count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (order[i], order[j]) = (order[j], order[i]);
            }

            for (var i = 0; i < count; i++)
            {
                // Jittered within the stratum rather than at its centre: a
                // centred hypercube is a lattice, and a lattice can alias
                // against a periodic response.
                points[i][d] = (order[i] + random.NextDouble()) / count;
            }
        }

        return points;
    }

    /// <summary>A DOE mapped onto a design space.</summary>
    public static IReadOnlyList<DesignPoint> Over(DesignSpace space, IReadOnlyList<double[]> unitPoints)
    {
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(unitPoints);

        return unitPoints.Select(p => new DesignPoint(space, p)).ToList();
    }

    /// <summary>
    /// The default orientation pass: Sobol where the dimension allows, a Latin
    /// hypercube beyond it.
    /// </summary>
    public static IReadOnlyList<DesignPoint> Orient(DesignSpace space, int count, int seed = 20260913)
    {
        ArgumentNullException.ThrowIfNull(space);

        var unit = space.Dimension <= MaxDimension
            ? Sobol(space.Dimension, count)
            : LatinHypercube(space.Dimension, count, seed);

        return Over(space, unit);
    }
}
