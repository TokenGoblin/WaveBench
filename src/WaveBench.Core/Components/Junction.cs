using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;

namespace WaveBench.Core.Components;

/// <summary>How a junction distributes pressure among its branches (plan §2.7).</summary>
public enum JunctionModel
{
    /// <summary>Benson constant-pressure: all branch ends share one static pressure.</summary>
    ConstantPressure,

    /// <summary>
    /// Constant-pressure solve plus Idelchik 90° tee loss corrections
    /// (<see cref="TeeJunctionLoss"/>). Requires exactly three branches with
    /// the side branch identified.
    /// </summary>
    TeeWithLosses,
}

/// <summary>
/// N-way pipe junction. Each step (before the ducts step) it solves the
/// common junction pressure from the linearised branch characteristics:
///   u_end = u_i + (p_i − p*)/(ρ_i·a_i)   (toward-junction frame)
///   Σ ρ_b A_b u_end,b = 0  →  p* = (Σ ρAu + Σ A·p/a) / (Σ A/a)
/// then hands each branch a ghost state: outflowing branches keep their own
/// entropy; inflowing branches receive the mass-weighted mixed enthalpy and
/// composition of the streams entering the junction. With
/// <see cref="JunctionModel.TeeWithLosses"/> the branch static pressures are
/// corrected by the Idelchik coefficients referenced to the combined-leg
/// dynamic head (explicit quasi-steady correction).
/// </summary>
public sealed class Junction
{
    private sealed class Branch(DuctSolver duct, bool leftEnd)
    {
        public DuctSolver Duct { get; } = duct;

        public bool LeftEnd { get; } = leftEnd;

        public Port Port { get; } = new();

        public double Sign => LeftEnd ? -1.0 : 1.0; // toward-junction sign on u

        /// <summary>Under-relaxed loss correction, Pa (signed).</summary>
        public double AppliedLoss { get; set; }

        /// <summary>Angle to the combined-leg axis, degrees; 90° is a plain tee.</summary>
        public double BranchAngleDeg { get; init; } = TeeJunctionLoss.RightAngleDeg;

        public int AdjacentCell => LeftEnd ? 0 : Duct.CellCount - 1;

        public double Area => LeftEnd ? Duct.Geometry.FaceArea[0] : Duct.Geometry.FaceArea[^1];
    }

    private sealed class Port : IEndBoundary
    {
        public PrimitiveState State { get; set; }

        public double[]? Composition { get; set; }

        public EndGhost Ghost(in GasState interior, double rhoInterior, ReadOnlySpan<double> yInterior,
            bool isLeftEnd, IGasModel gas) => new(State, Composition);
    }

    private readonly List<Branch> _branches = [];
    private readonly IGasModel _gas;
    private int _sideBranchIndex = -1;

    public Junction(IGasModel gas) => _gas = gas;

    public JunctionModel Model { get; set; } = JunctionModel.ConstantPressure;

    /// <summary>Junction pressure of the last update, Pa.</summary>
    public double Pressure { get; private set; }

    /// <summary>
    /// Adds a branch. <paramref name="branchAngleDeg"/> is the angle between
    /// this branch and the combined-leg axis and is used only for the side
    /// branch under <see cref="JunctionModel.TeeWithLosses"/>: 90° is a plain
    /// tee, a collector merging primaries is typically 10–30°.
    /// </summary>
    public void Connect(
        DuctSolver duct, bool leftEnd, bool isSideBranch = false,
        double branchAngleDeg = TeeJunctionLoss.RightAngleDeg)
    {
        if (duct.Gas != _gas)
        {
            throw new ArgumentException("All junction branches must share the gas model.");
        }

        if (branchAngleDeg is < 0.0 or > 180.0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(branchAngleDeg), branchAngleDeg, "Branch angle must be between 0° and 180°.");
        }

        var branch = new Branch(duct, leftEnd) { BranchAngleDeg = branchAngleDeg };
        _branches.Add(branch);
        if (isSideBranch)
        {
            _sideBranchIndex = _branches.Count - 1;
        }

        if (leftEnd)
        {
            duct.LeftBoundary = BoundaryKind.External;
            duct.LeftEnd = branch.Port;
        }
        else
        {
            duct.RightBoundary = BoundaryKind.External;
            duct.RightEnd = branch.Port;
        }
    }

    /// <summary>Solve the junction and set every branch's ghost state for the coming step.</summary>
    public void Update()
    {
        if (_branches.Count < 2)
        {
            throw new InvalidOperationException("A junction needs at least two branches.");
        }

        if (Model == JunctionModel.TeeWithLosses && (_branches.Count != 3 || _sideBranchIndex < 0))
        {
            throw new InvalidOperationException("Tee losses need exactly three branches with a marked side branch.");
        }

        Span<double> y = _gas.SpeciesCount > 0 ? stackalloc double[_gas.SpeciesCount] : default;

        var n = _branches.Count;
        Span<double> rho = stackalloc double[n];
        Span<double> p = stackalloc double[n];
        Span<double> a = stackalloc double[n];
        Span<double> uTo = stackalloc double[n];
        Span<double> gamma = stackalloc double[n];
        Span<double> r = stackalloc double[n];
        Span<double> t = stackalloc double[n];

        for (var b = 0; b < n; b++)
        {
            var branch = _branches[b];
            var s = branch.Duct.GetState(branch.AdjacentCell);
            var w = branch.Duct.GetPrimitive(branch.AdjacentCell);
            rho[b] = w.Rho;
            p[b] = s.P;
            a[b] = s.SoundSpeed;
            gamma[b] = s.Gamma;
            t[b] = s.T;
            r[b] = s.P / (w.Rho * s.T);
            uTo[b] = s.U * branch.Sign;
        }

        // Linearised constant-pressure solve.
        double num = 0, den = 0;
        for (var b = 0; b < n; b++)
        {
            var area = _branches[b].Area;
            num += rho[b] * area * uTo[b] + area * p[b] / a[b];
            den += area / a[b];
        }

        var pStar = num / den;
        Pressure = pStar;

        // Per-branch loss corrections (explicit, referenced to combined-leg head).
        Span<double> pBranch = stackalloc double[n];
        for (var b = 0; b < n; b++)
        {
            pBranch[b] = pStar;
        }

        if (Model == JunctionModel.TeeWithLosses)
        {
            ApplyTeeLosses(rho, uTo, pBranch);
        }

        // End velocities from the characteristics against the (corrected) pressure.
        Span<double> uEnd = stackalloc double[n];
        for (var b = 0; b < n; b++)
        {
            uEnd[b] = uTo[b] + (p[b] - pBranch[b]) / (rho[b] * a[b]);
        }

        // Mixed state of the streams entering the junction.
        double mDotIn = 0, hIn = 0;
        Span<double> yMix = _gas.SpeciesCount > 0 ? stackalloc double[_gas.SpeciesCount] : default;
        yMix.Clear();
        for (var b = 0; b < n; b++)
        {
            if (uEnd[b] <= 0)
            {
                continue;
            }

            var branch = _branches[b];
            var mDot = rho[b] * branch.Area * uEnd[b];
            var cp = gamma[b] * r[b] / (gamma[b] - 1.0);
            mDotIn += mDot;
            hIn += mDot * (cp * t[b] + 0.5 * uEnd[b] * uEnd[b]);
            if (_gas.SpeciesCount > 0)
            {
                FillY(branch, y);
                for (var k = 0; k < y.Length; k++)
                {
                    yMix[k] += mDot * y[k];
                }
            }
        }

        if (mDotIn > 0)
        {
            hIn /= mDotIn;
            for (var k = 0; k < yMix.Length; k++)
            {
                yMix[k] /= mDotIn;
            }
        }

        // Ghost states.
        for (var b = 0; b < n; b++)
        {
            var branch = _branches[b];
            var uPhysical = uEnd[b] * branch.Sign;

            if (uEnd[b] > 0 || mDotIn <= 0)
            {
                // Flow leaves this duct into the junction (or the junction is
                // stalled): keep the branch's own entropy.
                var rhoG = rho[b] * Math.Pow(pBranch[b] / p[b], 1.0 / gamma[b]);
                branch.Port.State = new PrimitiveState(rhoG, uPhysical, pBranch[b]);
                branch.Port.Composition = null;
            }
            else
            {
                // Junction feeds this duct: mixed enthalpy and composition.
                var cp = gamma[b] * r[b] / (gamma[b] - 1.0);
                var tMix = Math.Max(150.0, (hIn - 0.5 * uEnd[b] * uEnd[b]) / cp);
                var rMix = MixGasConstant(yMix, r[b]);
                var rhoG = pBranch[b] / (rMix * tMix);
                branch.Port.State = new PrimitiveState(rhoG, uPhysical, pBranch[b]);
                branch.Port.Composition = _gas.SpeciesCount > 0 ? yMix.ToArray() : null;
            }
        }
    }

    private void ApplyTeeLosses(ReadOnlySpan<double> rho, ReadOnlySpan<double> uTo, Span<double> pBranch)
    {
        // Identify the combined leg: the largest |flow| among the two
        // non-side branches; q = |side flow| / |combined flow|.
        var side = _sideBranchIndex;
        var others = Enumerable.Range(0, _branches.Count).Where(i => i != side).ToArray();
        var flows = new double[_branches.Count];
        for (var b = 0; b < _branches.Count; b++)
        {
            flows[b] = rho[b] * _branches[b].Area * uTo[b];
        }

        var combined = Math.Abs(flows[others[0]]) >= Math.Abs(flows[others[1]]) ? others[0] : others[1];
        var combinedFlow = Math.Abs(flows[combined]);
        if (combinedFlow < 1e-9)
        {
            return;
        }

        var q = Math.Min(1.0, Math.Abs(flows[side]) / combinedFlow);
        var areaRatio = _branches[side].Area / _branches[combined].Area;
        var vCombined = combinedFlow / (rho[combined] * _branches[combined].Area);
        var head = 0.5 * rho[combined] * vCombined * vCombined;

        var combining = flows[side] > 0; // side branch feeds the junction

        var angle = _branches[side].BranchAngleDeg;
        var xiSide = combining
            ? TeeJunctionLoss.CombiningBranch(q, areaRatio, angle)
            : TeeJunctionLoss.DividingBranch(q, areaRatio, angle);
        var straight = others.First(i => i != combined);
        var xiStraight = combining
            ? TeeJunctionLoss.CombiningStraight(q)
            : TeeJunctionLoss.DividingStraight(q);

        // Idelchik's ξ are leg-pair coefficients vs the combined leg: hold the
        // combined leg at the node pressure and offset each other leg by its
        // full pair loss. A stream pushing into the junction fights the loss
        // (its end sees p* + ξ·head); a stream drawn out receives p* − ξ·head.
        // Applied with under-relaxation: the quasi-steady correction ramped in
        // over ~100 steps keeps the explicit coupling stable while converging
        // to the full published coefficient at steady state.
        const double relax = 0.02;
        var targetSide = Math.Sign(flows[side]) * xiSide * head;
        var targetStraight = Math.Sign(flows[straight]) * xiStraight * head;
        var bSide = _branches[side];
        var bStraight = _branches[straight];
        bSide.AppliedLoss += relax * (targetSide - bSide.AppliedLoss);
        bStraight.AppliedLoss += relax * (targetStraight - bStraight.AppliedLoss);
        pBranch[side] += bSide.AppliedLoss;
        pBranch[straight] += bStraight.AppliedLoss;
    }

    private double MixGasConstant(ReadOnlySpan<double> yMix, double fallback) =>
        _gas is MultiSpeciesGasModel multi && yMix.Length > 0 ? multi.GasConstant(yMix) : fallback;

    private void FillY(Branch branch, Span<double> y)
    {
        for (var k = 0; k < y.Length; k++)
        {
            y[k] = branch.Duct.GetMassFraction(k, branch.AdjacentCell);
        }
    }
}
