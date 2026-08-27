using WaveBench.Core.Components;
using WaveBench.Core.Numerics;

namespace WaveBench.Core.Solver;

public enum BoundaryKind
{
    /// <summary>Zero-gradient outflow.</summary>
    Transmissive,

    /// <summary>Solid wall (mirror with velocity sign flip).</summary>
    Reflective,

    /// <summary>Wrap-around; must be set on both ends.</summary>
    Periodic,

    /// <summary>Ghost states supplied by an attached <see cref="IEndBoundary"/>.</summary>
    External,
}

/// <summary>
/// Quasi-1D unsteady compressible flow in a duct (plan §2.1):
/// MUSCL-Hancock + HLLC (plan §5.1) on the area-weighted finite-volume form,
/// with species transport (§2.2), Haaland wall friction, Colburn wall heat
/// transfer and an optional per-cell wall thermal node (§2.9).
///
/// Well-balancedness: face areas appear identically in the conservative
/// update, the Hancock half-step and the discrete p·dA/dx source, so a
/// uniform state at rest in a taper is preserved to machine precision — the
/// classic silent killer this class of code must not have (plan §5.1).
///
/// The friction momentum sink deliberately leaves total energy untouched:
/// in conservation form that converts the lost kinetic energy into internal
/// energy, which is the physical dissipation heating.
/// </summary>
public sealed class DuctSolver
{
    private const int Ghost = 2;

    private readonly DuctGeometry _geometry;
    private readonly IGasModel _gas;
    private readonly int _n;
    private readonly double _dx;

    private readonly double[] _rho;
    private readonly double[] _mom;
    private readonly double[] _ener;
    private readonly double[][] _rhoY;          // conserved ρ·Y_k

    private readonly double[] _wU;
    private readonly double[] _wP;
    private readonly double[] _t;
    private readonly double[] _gamma;
    private readonly double[] _a;
    private readonly double[][] _wY;

    private readonly double[] _faceLRho;
    private readonly double[] _faceLU;
    private readonly double[] _faceLP;
    private readonly double[] _faceRRho;
    private readonly double[] _faceRU;
    private readonly double[] _faceRP;
    private readonly double[][] _faceLY;
    private readonly double[][] _faceRY;

    private readonly double[] _fluxRho;
    private readonly double[] _fluxMom;
    private readonly double[] _fluxEner;

    private readonly double[] _hWall;           // per-cell inner heat-transfer coefficient

    // Devirtualized fast path: when the gas is the sealed perfect-gas model,
    // the hot loops use closed-form EOS math instead of interface dispatch
    // (~8 virtual calls per cell per step otherwise — measured dominant).
    private readonly bool _isPerfect;
    private readonly double _pgGamma;
    private readonly double _pgR;

    public DuctSolver(DuctGeometry geometry, IGasModel gas)
    {
        _geometry = geometry;
        _gas = gas;
        if (gas is PerfectGasModel perfect)
        {
            _isPerfect = true;
            _pgGamma = perfect.Gas.Gamma;
            _pgR = perfect.Gas.SpecificGasConstant;
        }
        _n = geometry.CellCount;
        _dx = geometry.CellSize;

        var total = _n + 2 * Ghost;
        _rho = new double[total];
        _mom = new double[total];
        _ener = new double[total];
        _wU = new double[total];
        _wP = new double[total];
        _t = new double[total];
        _gamma = new double[total];
        _a = new double[total];
        _faceLRho = new double[total];
        _faceLU = new double[total];
        _faceLP = new double[total];
        _faceRRho = new double[total];
        _faceRU = new double[total];
        _faceRP = new double[total];
        _fluxRho = new double[_n + 1];
        _fluxMom = new double[_n + 1];
        _fluxEner = new double[_n + 1];
        _hWall = new double[_n];

        _roughnessTerm = new double[_n];
        for (var i = 0; i < _n; i++)
        {
            _roughnessTerm[i] = PipeFlowPhysics.HaalandRoughnessTerm(
                _geometry.Roughness / _geometry.HydraulicDiameter[i]);
        }

        var s = gas.SpeciesCount;
        _rhoY = new double[s][];
        _wY = new double[s][];
        _faceLY = new double[s][];
        _faceRY = new double[s][];
        for (var k = 0; k < s; k++)
        {
            _rhoY[k] = new double[total];
            _wY[k] = new double[total];
            _faceLY[k] = new double[total];
            _faceRY[k] = new double[total];
        }

        Array.Fill(_t, 300.0);
    }

    public DuctGeometry Geometry => _geometry;

    public IGasModel Gas => _gas;

    public int CellCount => _n;

    public double CellSize => _dx;

    public double Time { get; private set; }

    /// <summary>CFL number, ≤ 0.8 per plan §5.1.</summary>
    public double Cfl { get; set; } = 0.8;

    public SlopeLimiterKind Limiter { get; set; } = SlopeLimiterKind.VanLeer;

    public BoundaryKind LeftBoundary { get; set; } = BoundaryKind.Transmissive;

    public BoundaryKind RightBoundary { get; set; } = BoundaryKind.Transmissive;

    /// <summary>External boundary handler for <see cref="BoundaryKind.External"/> ends.</summary>
    public IEndBoundary? LeftEnd { get; set; }

    public IEndBoundary? RightEnd { get; set; }

    /// <summary>
    /// Direct interface-flux override at an end face (orifice/plenum-port
    /// coupling): (mass, momentum, energy) flux in +x sense, plus the
    /// composition carried by incoming mass. Persists until changed; the
    /// network orchestrator sets it before each step.
    /// </summary>
    public (double FRho, double FMom, double FEner)? LeftFluxOverride { get; set; }

    public (double FRho, double FMom, double FEner)? RightFluxOverride { get; set; }

    public double[]? LeftFluxComposition { get; set; }

    public double[]? RightFluxComposition { get; set; }

    /// <summary>Haaland/Darcy wall friction source (plan §2.1).</summary>
    public bool FrictionEnabled { get; set; }

    /// <summary>Colburn wall heat transfer; requires an attached wall model.</summary>
    public bool HeatTransferEnabled => Wall is not null;

    public WallThermalModel? Wall { get; private set; }

    public double Prandtl
    {
        get => _prandtl;
        set
        {
            _prandtl = value;
            PrandtlFactor = PipeFlowPhysics.PrandtlFactor(value);
        }
    }

    private double _prandtl = 0.71;

    /// <summary>Pr^(−2/3), kept in step with <see cref="Prandtl"/> so the source-term loop never evaluates it.</summary>
    public double PrandtlFactor { get; private set; } = PipeFlowPhysics.PrandtlFactor(0.71);

    /// <summary>(ε/3.7D)^1.11 per cell — geometry only, so it never changes during a run.</summary>
    private double[] _roughnessTerm = [];

    /// <summary>Pulsating-flow enhancement on Colburn h (empirical, plan §2.1).</summary>
    public double HeatTransferEnhancement { get; set; } = 1.3;

    /// <summary>
    /// Injector-style mass sources (plan §2.7): vapour mass of one species
    /// added to a cell at a given temperature, at zero axial momentum.
    /// Requires the multi-species gas model. Rates are settable per step
    /// (injection timing/targeting arrives with the engine model).
    /// </summary>
    public List<DuctMassSource> MassSources { get; } = [];

    public void AttachWall(WallThermalModel wall)
    {
        if (wall.Temperature.Length != _n)
        {
            throw new ArgumentException("Wall model cell count must match the duct.");
        }

        Wall = wall;
    }

    public double CellCentre(int i) => (i + 0.5) * _dx;

    public void SetState(int i, in PrimitiveState w, ReadOnlySpan<double> massFractions = default)
    {
        var c = i + Ghost;
        Span<double> y = _gas.SpeciesCount > 0 ? stackalloc double[_gas.SpeciesCount] : default;
        if (_gas.SpeciesCount > 0)
        {
            if (massFractions.Length != _gas.SpeciesCount)
            {
                throw new ArgumentException("Mass fractions must match the gas model's species count.");
            }

            massFractions.CopyTo(y);
        }

        _rho[c] = w.Rho;
        _mom[c] = w.Rho * w.U;
        _ener[c] = _gas.TotalEnergy(w.Rho, w.U, w.P, y);
        for (var k = 0; k < _gas.SpeciesCount; k++)
        {
            _rhoY[k][c] = w.Rho * y[k];
        }
    }

    public PrimitiveState GetPrimitive(int i)
    {
        var state = GetState(i);
        return new PrimitiveState(_rho[i + Ghost], state.U, state.P);
    }

    /// <summary>
    /// Static pressure of one cell, Pa — the probe fast path. Avoids the full
    /// EOS state recovery when only pressure is wanted (capture runs every
    /// step, so this is a hot path).
    /// </summary>
    public double GetPressure(int i)
    {
        var c = i + Ghost;
        if (_isPerfect)
        {
            return (_pgGamma - 1.0) * (_ener[c] - 0.5 * _mom[c] * _mom[c] / _rho[c]);
        }

        Span<double> y = _gas.SpeciesCount > 0 ? stackalloc double[_gas.SpeciesCount] : default;
        FillMassFractions(c, y);
        return _gas.FromConserved(_rho[c], _mom[c], _ener[c], y, _t[c]).P;
    }

    /// <summary>
    /// Axial velocity of one cell, m/s — the probe fast path, u = (ρu)/ρ with
    /// no EOS state recovery. Signed: positive is the +x sense.
    /// </summary>
    public double GetVelocity(int i)
    {
        var c = i + Ghost;
        return _mom[c] / _rho[c];
    }

    public GasState GetState(int i)
    {
        var c = i + Ghost;
        Span<double> y = _gas.SpeciesCount > 0 ? stackalloc double[_gas.SpeciesCount] : default;
        FillMassFractions(c, y);
        return _gas.FromConserved(_rho[c], _mom[c], _ener[c], y, _t[c]);
    }

    public double GetMassFraction(int speciesIndex, int i)
    {
        var c = i + Ghost;
        return _rhoY[speciesIndex][c] / _rho[c];
    }

    public (double Mass, double Momentum, double Energy) ConservedTotals()
    {
        double m = 0, mom = 0, e = 0;
        for (var i = 0; i < _n; i++)
        {
            var v = _geometry.CellArea[i] * _dx;
            var c = i + Ghost;
            m += _rho[c] * v;
            mom += _mom[c] * v;
            e += _ener[c] * v;
        }

        return (m, mom, e);
    }

    public double SpeciesTotalMass(int speciesIndex)
    {
        var total = 0.0;
        for (var i = 0; i < _n; i++)
        {
            total += _rhoY[speciesIndex][i + Ghost] * _geometry.CellArea[i] * _dx;
        }

        return total;
    }

    public void Advance(double tEnd)
    {
        while (Time < tEnd)
        {
            var dt = Math.Min(StableTimestep(), tEnd - Time);
            Step(dt);
        }
    }

    private double _maxWaveSpeed;

    public double StableTimestep()
    {
        // The wave-speed maximum is cached by ComputePrimitives during each
        // step; the one-step lag is standard practice and covered by the CFL
        // margin. Only the very first call pays for a fresh evaluation.
        if (_maxWaveSpeed <= 0.0)
        {
            ComputePrimitives();
        }

        return Cfl * _dx / _maxWaveSpeed;
    }

    public void Step(double dt)
    {
        FillGhostCells();
        ComputePrimitives();
        ReconstructAndEvolveFaces(dt);
        ComputeInterfaceFluxes();
        UpdateConserved(dt);
        ApplySources(dt);
        Time += dt;
    }

    private void FillMassFractions(int c, Span<double> y)
    {
        for (var k = 0; k < y.Length; k++)
        {
            y[k] = _rhoY[k][c] / _rho[c];
        }
    }

    private void FillGhostCells()
    {
        var first = Ghost;
        var last = Ghost + _n - 1;

        if (LeftBoundary == BoundaryKind.Periodic || RightBoundary == BoundaryKind.Periodic)
        {
            if (LeftBoundary != RightBoundary)
            {
                throw new InvalidOperationException("Periodic boundaries must be set on both ends.");
            }

            for (var k = 0; k < Ghost; k++)
            {
                Copy(last - Ghost + 1 + k, k);
                Copy(first + k, last + 1 + k);
            }

            return;
        }

        for (var k = 0; k < Ghost; k++)
        {
            var leftGhost = Ghost - 1 - k;
            var leftMirror = first + k;
            var rightGhost = last + 1 + k;
            var rightMirror = last - k;

            if (LeftBoundary == BoundaryKind.Reflective)
            {
                Copy(leftMirror, leftGhost);
                _mom[leftGhost] = -_mom[leftGhost];
            }
            else
            {
                Copy(first, leftGhost);
            }

            if (RightBoundary == BoundaryKind.Reflective)
            {
                Copy(rightMirror, rightGhost);
                _mom[rightGhost] = -_mom[rightGhost];
            }
            else
            {
                Copy(last, rightGhost);
            }
        }

        if (LeftBoundary == BoundaryKind.External)
        {
            ApplyExternalGhosts(LeftEnd ?? throw new InvalidOperationException("LeftEnd boundary not set."),
                interiorCell: first, ghost0: 0, ghost1: 1, isLeftEnd: true);
        }

        if (RightBoundary == BoundaryKind.External)
        {
            ApplyExternalGhosts(RightEnd ?? throw new InvalidOperationException("RightEnd boundary not set."),
                interiorCell: last, ghost0: last + 1, ghost1: last + 2, isLeftEnd: false);
        }

        void Copy(int from, int to)
        {
            _rho[to] = _rho[from];
            _mom[to] = _mom[from];
            _ener[to] = _ener[from];
            _t[to] = _t[from];
            for (var s = 0; s < _gas.SpeciesCount; s++)
            {
                _rhoY[s][to] = _rhoY[s][from];
            }
        }
    }

    private void ApplyExternalGhosts(IEndBoundary boundary, int interiorCell, int ghost0, int ghost1, bool isLeftEnd)
    {
        Span<double> y = _gas.SpeciesCount > 0 ? stackalloc double[_gas.SpeciesCount] : default;
        FillMassFractions(interiorCell, y);
        var interior = _gas.FromConserved(_rho[interiorCell], _mom[interiorCell], _ener[interiorCell], y, _t[interiorCell]);

        var ghost = boundary.Ghost(interior, _rho[interiorCell], y, isLeftEnd, _gas);
        Span<double> yGhost = _gas.SpeciesCount > 0 ? stackalloc double[_gas.SpeciesCount] : default;
        if (_gas.SpeciesCount > 0)
        {
            if (ghost.MassFractions is { } provided)
            {
                if (provided.Length != _gas.SpeciesCount)
                {
                    throw new InvalidOperationException("Boundary composition length mismatch.");
                }

                provided.CopyTo(yGhost);
            }
            else
            {
                y.CopyTo(yGhost);
            }
        }

        var w = ghost.State;
        var mom = w.Rho * w.U;
        var ener = _gas.TotalEnergy(w.Rho, w.U, w.P, yGhost);
        foreach (var g in (ReadOnlySpan<int>)[ghost0, ghost1])
        {
            _rho[g] = w.Rho;
            _mom[g] = mom;
            _ener[g] = ener;
            for (var k = 0; k < _gas.SpeciesCount; k++)
            {
                _rhoY[k][g] = w.Rho * yGhost[k];
            }
        }
    }

    private void ComputePrimitives()
    {
        var maxSpeed = 0.0;

        if (_isPerfect)
        {
            var g1 = _pgGamma - 1.0;
            for (var c = 0; c < _rho.Length; c++)
            {
                var rho = _rho[c];
                var u = _mom[c] / rho;
                var p = g1 * (_ener[c] - 0.5 * _mom[c] * u);
                _wU[c] = u;
                _wP[c] = p;
                _t[c] = p / (rho * _pgR);
                _gamma[c] = _pgGamma;
                var a = Math.Sqrt(_pgGamma * p / rho);
                _a[c] = a;
                var speed = Math.Abs(u) + a;
                if (speed > maxSpeed)
                {
                    maxSpeed = speed;
                }
            }

            _maxWaveSpeed = maxSpeed;
            return;
        }

        Span<double> y = _gas.SpeciesCount > 0 ? stackalloc double[_gas.SpeciesCount] : default;
        for (var c = 0; c < _rho.Length; c++)
        {
            FillMassFractions(c, y);
            for (var k = 0; k < y.Length; k++)
            {
                _wY[k][c] = y[k];
            }

            var state = _gas.FromConserved(_rho[c], _mom[c], _ener[c], y, _t[c]);
            _wU[c] = state.U;
            _wP[c] = state.P;
            _t[c] = state.T;
            _gamma[c] = state.Gamma;
            _a[c] = state.SoundSpeed;

            var speed = Math.Abs(state.U) + state.SoundSpeed;
            if (speed > maxSpeed)
            {
                maxSpeed = speed;
            }
        }

        _maxWaveSpeed = maxSpeed;
    }

    private void ReconstructAndEvolveFaces(double dt)
    {
        Span<double> yL = _gas.SpeciesCount > 0 ? stackalloc double[_gas.SpeciesCount] : default;
        Span<double> yR = _gas.SpeciesCount > 0 ? stackalloc double[_gas.SpeciesCount] : default;

        // Extend face areas onto ghost cells (constant continuation) so the
        // Hancock half-step of the cells adjacent to the boundary is defined.
        for (var c = 1; c <= _n + 2; c++)
        {
            var i = c - Ghost;
            var aLFace = _geometry.FaceArea[Math.Clamp(i, 0, _n)];
            var aRFace = _geometry.FaceArea[Math.Clamp(i + 1, 0, _n)];
            var aCell = 0.5 * (aLFace + aRFace);
            var halfDtOverAdx = 0.5 * dt / (aCell * _dx);

            var sRho = SlopeLimiters.Limit(Limiter, _rho[c] - _rho[c - 1], _rho[c + 1] - _rho[c]);
            var sU = SlopeLimiters.Limit(Limiter, _wU[c] - _wU[c - 1], _wU[c + 1] - _wU[c]);
            var sP = SlopeLimiters.Limit(Limiter, _wP[c] - _wP[c - 1], _wP[c + 1] - _wP[c]);

            var lRho = _rho[c] - 0.5 * sRho;
            var rRho = _rho[c] + 0.5 * sRho;
            var lP = _wP[c] - 0.5 * sP;
            var rP = _wP[c] + 0.5 * sP;

            if (lRho <= 0 || rRho <= 0 || lP <= 0 || rP <= 0)
            {
                sRho = sU = sP = 0.0;
                lRho = rRho = _rho[c];
                lP = rP = _wP[c];
            }

            var lU = _wU[c] - 0.5 * sU;
            var rU = _wU[c] + 0.5 * sU;

            // Species face values: limited reconstruction, clamped and
            // normalised so Σ Y = 1 exactly on each face.
            ReconstructSpeciesFaces(c);

            for (var k = 0; k < _gas.SpeciesCount; k++)
            {
                yL[k] = _faceLY[k][c];
                yR[k] = _faceRY[k][c];
            }

            // Hancock half-step in area-weighted conservative form, including
            // the p·dA source with the same face areas (well-balanced at rest).
            double eL, eR;
            if (_isPerfect)
            {
                eL = lP / (_pgGamma - 1.0) + 0.5 * lRho * lU * lU;
                eR = rP / (_pgGamma - 1.0) + 0.5 * rRho * rU * rU;
            }
            else
            {
                eL = _gas.TotalEnergy(lRho, lU, lP, yL);
                eR = _gas.TotalEnergy(rRho, rU, rP, yR);
            }
            var (flRho, flMom, flEner) = Flux(lRho, lU, lP, eL);
            var (frRho, frMom, frEner) = Flux(rRho, rU, rP, eR);

            var dRho = halfDtOverAdx * (aLFace * flRho - aRFace * frRho);
            var dMom = halfDtOverAdx * (aLFace * flMom - aRFace * frMom)
                       + halfDtOverAdx * _wP[c] * (aRFace - aLFace);
            var dEner = halfDtOverAdx * (aLFace * flEner - aRFace * frEner);

            EvolveFace(lRho, lU, lP, eL, yL, dRho, dMom, dEner, c, _faceLRho, _faceLU, _faceLP);
            EvolveFace(rRho, rU, rP, eR, yR, dRho, dMom, dEner, c, _faceRRho, _faceRU, _faceRP);
        }

        static (double, double, double) Flux(double rho, double u, double p, double e) =>
            (rho * u, rho * u * u + p, u * (e + p));
    }

    private void ReconstructSpeciesFaces(int c)
    {
        if (_gas.SpeciesCount == 0)
        {
            return;
        }

        double sumL = 0, sumR = 0;
        for (var k = 0; k < _gas.SpeciesCount; k++)
        {
            var w = _wY[k];
            var slope = SlopeLimiters.Limit(Limiter, w[c] - w[c - 1], w[c + 1] - w[c]);
            var l = Math.Clamp(w[c] - 0.5 * slope, 0.0, 1.0);
            var r = Math.Clamp(w[c] + 0.5 * slope, 0.0, 1.0);
            _faceLY[k][c] = l;
            _faceRY[k][c] = r;
            sumL += l;
            sumR += r;
        }

        for (var k = 0; k < _gas.SpeciesCount; k++)
        {
            _faceLY[k][c] = sumL > 0 ? _faceLY[k][c] / sumL : _wY[k][c];
            _faceRY[k][c] = sumR > 0 ? _faceRY[k][c] / sumR : _wY[k][c];
        }
    }

    private void EvolveFace(
        double rho0, double u0, double p0, double e0, ReadOnlySpan<double> y,
        double dRho, double dMom, double dEner,
        int c, double[] outRho, double[] outU, double[] outP)
    {
        var rho = rho0 + dRho;
        var mom = rho0 * u0 + dMom;
        var ener = e0 + dEner;

        if (rho <= 0)
        {
            outRho[c] = rho0;
            outU[c] = u0;
            outP[c] = p0;
            return;
        }

        double u, p;
        if (_isPerfect)
        {
            u = mom / rho;
            p = (_pgGamma - 1.0) * (ener - 0.5 * mom * u);
        }
        else
        {
            var state = _gas.FromConserved(rho, mom, ener, y, _t[c]);
            u = state.U;
            p = state.P;
        }

        if (p <= 0)
        {
            outRho[c] = rho0;
            outU[c] = u0;
            outP[c] = p0;
            return;
        }

        outRho[c] = rho;
        outU[c] = u;
        outP[c] = p;
    }

    private void ComputeInterfaceFluxes()
    {
        Span<double> y = _gas.SpeciesCount > 0 ? stackalloc double[_gas.SpeciesCount] : default;
        for (var j = 0; j <= _n; j++)
        {
            var c = j + 1;
            var (lRho, lU, lP) = (_faceRRho[c], _faceRU[c], _faceRP[c]);
            var (rRho, rU, rP) = (_faceLRho[c + 1], _faceLU[c + 1], _faceLP[c + 1]);

            double eL, gL, eR, gR;
            if (_isPerfect)
            {
                var g1 = _pgGamma - 1.0;
                eL = lP / g1 + 0.5 * lRho * lU * lU;
                eR = rP / g1 + 0.5 * rRho * rU * rU;
                gL = _pgGamma;
                gR = _pgGamma;
            }
            else
            {
                for (var k = 0; k < y.Length; k++)
                {
                    y[k] = _faceRY[k][c];
                }

                eL = _gas.TotalEnergy(lRho, lU, lP, y);
                gL = _gas.Gamma(lRho, lP, y);

                for (var k = 0; k < y.Length; k++)
                {
                    y[k] = _faceLY[k][c + 1];
                }

                eR = _gas.TotalEnergy(rRho, rU, rP, y);
                gR = _gas.Gamma(rRho, rP, y);
            }

            var aL = SoundSpeedOf(lRho, lP, gL);
            var aR = SoundSpeedOf(rRho, rP, gR);

            (_fluxRho[j], _fluxMom[j], _fluxEner[j]) = HllcFlux.Compute(
                new HllcSide(lRho, lU, lP, eL, aL, gL),
                new HllcSide(rRho, rU, rP, eR, aR, gR));
        }

        if (LeftFluxOverride is { } left)
        {
            (_fluxRho[0], _fluxMom[0], _fluxEner[0]) = left;
        }

        if (RightFluxOverride is { } right)
        {
            (_fluxRho[_n], _fluxMom[_n], _fluxEner[_n]) = right;
        }

        static double SoundSpeedOf(double rho, double p, double gamma) => Math.Sqrt(gamma * p / rho);
    }

    private void UpdateConserved(double dt)
    {
        for (var i = 0; i < _n; i++)
        {
            var c = i + Ghost;
            var aL = _geometry.FaceArea[i];
            var aR = _geometry.FaceArea[i + 1];
            var invVol = 1.0 / (_geometry.CellArea[i] * _dx);
            var f = dt * invVol;

            // Species first: needs the pre-update mass fluxes and upwind faces.
            for (var k = 0; k < _gas.SpeciesCount; k++)
            {
                var yLeftUp = _fluxRho[i] >= 0 ? _faceRY[k][i + 1] : _faceLY[k][i + 2];
                var yRightUp = _fluxRho[i + 1] >= 0 ? _faceRY[k][i + 2] : _faceLY[k][i + 3];

                // Flux-override ends: incoming mass carries the composition the
                // external component prescribes.
                if (i == 0 && LeftFluxOverride is not null && _fluxRho[0] >= 0 && LeftFluxComposition is { } yIn)
                {
                    yLeftUp = yIn[k];
                }

                if (i == _n - 1 && RightFluxOverride is not null && _fluxRho[_n] < 0 && RightFluxComposition is { } yInR)
                {
                    yRightUp = yInR[k];
                }

                _rhoY[k][c] -= f * (aR * _fluxRho[i + 1] * yRightUp - aL * _fluxRho[i] * yLeftUp);
            }

            _rho[c] -= f * (aR * _fluxRho[i + 1] - aL * _fluxRho[i]);
            _mom[c] -= f * (aR * _fluxMom[i + 1] - aL * _fluxMom[i]);
            _mom[c] += f * _wP[c] * (aR - aL); // well-balanced p·dA/dx source
            _ener[c] -= f * (aR * _fluxEner[i + 1] - aL * _fluxEner[i]);

            // Keep Σ(ρY) ≡ ρ exactly: clamp and renormalise the species vector.
            if (_gas.SpeciesCount > 0)
            {
                var sum = 0.0;
                for (var k = 0; k < _gas.SpeciesCount; k++)
                {
                    if (_rhoY[k][c] < 0)
                    {
                        _rhoY[k][c] = 0;
                    }

                    sum += _rhoY[k][c];
                }

                if (sum > 0)
                {
                    var scale = _rho[c] / sum;
                    for (var k = 0; k < _gas.SpeciesCount; k++)
                    {
                        _rhoY[k][c] *= scale;
                    }
                }
            }
        }
    }

    private void ApplySources(double dt)
    {
        foreach (var source in MassSources)
        {
            if (source.MassRate <= 0)
            {
                continue;
            }

            if (_gas is not MultiSpeciesGasModel multi)
            {
                throw new InvalidOperationException("Injector mass sources require the multi-species gas model.");
            }

            var c = source.Cell + Ghost;
            var dmPerVolume = source.MassRate * dt / (_geometry.CellArea[source.Cell] * _dx);
            _rho[c] += dmPerVolume;
            _rhoY[source.SpeciesIndex][c] += dmPerVolume;
            _ener[c] += dmPerVolume * multi.SpeciesEnthalpy(source.SpeciesIndex, source.Temperature);
        }

        if (!FrictionEnabled && !HeatTransferEnabled)
        {
            return;
        }

        Span<double> y = _gas.SpeciesCount > 0 ? stackalloc double[_gas.SpeciesCount] : default;

        for (var i = 0; i < _n; i++)
        {
            var c = i + Ghost;
            var d = _geometry.HydraulicDiameter[i];
            var rho = _rho[c];
            var u = _mom[c] / rho;
            var t = _t[c];

            var mu = PipeFlowPhysics.SutherlandViscosity(t);
            var re = rho * Math.Abs(u) * d / mu;

            // Roughness term is geometry only — precomputed per cell in
            // RebuildRoughnessTerms, because this is the innermost loop of the
            // whole solver and Math.Pow here was measurable.
            var fD = PipeFlowPhysics.DarcyFrictionFactorPrecomputed(re, _roughnessTerm[i]);

            if (FrictionEnabled)
            {
                // S_mom = −(f_D/2)·ρu|u|/D (plan §2.1). Energy untouched: the
                // removed kinetic energy becomes internal energy (dissipation).
                _mom[c] -= dt * fD / (2.0 * d) * rho * u * Math.Abs(u);
            }

            if (HeatTransferEnabled)
            {
                FillMassFractions(c, y);
                var cp = _gas.Cp(rho, _wP[c], y);
                var h = PipeFlowPhysics.ColburnPrecomputed(
                    fD, rho, u, cp, PrandtlFactor, HeatTransferEnhancement);
                _hWall[i] = h;
                _ener[c] += dt * h * 4.0 / d * (Wall!.Temperature[i] - t);
            }
        }

        if (HeatTransferEnabled)
        {
            // Gas temperatures are one step stale here; the wall time constant
            // is orders of magnitude longer, so this is immaterial.
            Wall!.Update(dt, _hWall, _t.AsSpan(Ghost, _n));
        }
    }
}
