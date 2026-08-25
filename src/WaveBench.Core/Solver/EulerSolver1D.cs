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
}

/// <summary>
/// Second-order finite-volume solver for the 1D Euler equations on a uniform
/// constant-area grid: MUSCL reconstruction on primitive variables with a
/// slope limiter, Hancock half-step, HLLC interface fluxes (plan §5.1;
/// Toro ch. 14). Conservative update; CFL-controlled global timestep.
/// Variable area, friction, heat transfer and species arrive in Phase 3.
/// </summary>
public sealed class EulerSolver1D
{
    private const int Ghost = 2;

    private readonly int _n;
    private readonly double _dx;
    private readonly PerfectGas _gas;

    // Conserved state including ghosts (struct-of-arrays, plan §5.7).
    private readonly double[] _rho;
    private readonly double[] _mom;
    private readonly double[] _ener;

    // Primitive scratch, slopes, Hancock-evolved face states, interface fluxes.
    private readonly double[] _wRho;
    private readonly double[] _wU;
    private readonly double[] _wP;
    private readonly double[] _faceLRho;
    private readonly double[] _faceLU;
    private readonly double[] _faceLP;
    private readonly double[] _faceRRho;
    private readonly double[] _faceRU;
    private readonly double[] _faceRP;
    private readonly double[] _fluxRho;
    private readonly double[] _fluxMom;
    private readonly double[] _fluxEner;

    public EulerSolver1D(int cellCount, double cellSize, PerfectGas gas)
    {
        if (cellCount < 4)
        {
            throw new ArgumentOutOfRangeException(nameof(cellCount), "Need at least 4 cells.");
        }

        _n = cellCount;
        _dx = cellSize;
        _gas = gas;

        var total = cellCount + 2 * Ghost;
        _rho = new double[total];
        _mom = new double[total];
        _ener = new double[total];
        _wRho = new double[total];
        _wU = new double[total];
        _wP = new double[total];
        _faceLRho = new double[total];
        _faceLU = new double[total];
        _faceLP = new double[total];
        _faceRRho = new double[total];
        _faceRU = new double[total];
        _faceRP = new double[total];
        _fluxRho = new double[cellCount + 1];
        _fluxMom = new double[cellCount + 1];
        _fluxEner = new double[cellCount + 1];
    }

    public int CellCount => _n;

    public double CellSize => _dx;

    public double Time { get; private set; }

    /// <summary>CFL number, ≤ 0.8 per plan §5.1.</summary>
    public double Cfl { get; set; } = 0.8;

    public SlopeLimiterKind Limiter { get; set; } = SlopeLimiterKind.VanLeer;

    public BoundaryKind LeftBoundary { get; set; } = BoundaryKind.Transmissive;

    public BoundaryKind RightBoundary { get; set; } = BoundaryKind.Transmissive;

    /// <summary>Cell-centre coordinate of interior cell i (0-based), x = (i + ½)·Δx.</summary>
    public double CellCentre(int i) => (i + 0.5) * _dx;

    public void SetPrimitive(int i, in PrimitiveState w)
    {
        var c = i + Ghost;
        _rho[c] = w.Rho;
        _mom[c] = w.Rho * w.U;
        _ener[c] = _gas.TotalEnergy(w.Rho, w.U, w.P);
    }

    public PrimitiveState GetPrimitive(int i)
    {
        var c = i + Ghost;
        var u = _mom[c] / _rho[c];
        var p = _gas.Pressure(_rho[c], _mom[c], _ener[c]);
        return new PrimitiveState(_rho[c], u, p);
    }

    /// <summary>Total mass, momentum and energy over the interior (× Δx), for conservation checks.</summary>
    public (double Mass, double Momentum, double Energy) ConservedTotals()
    {
        double m = 0, mom = 0, e = 0;
        for (var c = Ghost; c < Ghost + _n; c++)
        {
            m += _rho[c];
            mom += _mom[c];
            e += _ener[c];
        }

        return (m * _dx, mom * _dx, e * _dx);
    }

    /// <summary>Advances to exactly tEnd with CFL-limited steps.</summary>
    public void Advance(double tEnd)
    {
        while (Time < tEnd)
        {
            var dt = Math.Min(StableTimestep(), tEnd - Time);
            Step(dt);
        }
    }

    /// <summary>Δt = CFL · Δx / max(|u| + a) over the interior (plan §5.1).</summary>
    public double StableTimestep()
    {
        var maxSpeed = 0.0;
        for (var c = Ghost; c < Ghost + _n; c++)
        {
            var u = _mom[c] / _rho[c];
            var p = _gas.Pressure(_rho[c], _mom[c], _ener[c]);
            var speed = Math.Abs(u) + _gas.SoundSpeed(_rho[c], p);
            if (speed > maxSpeed)
            {
                maxSpeed = speed;
            }
        }

        return Cfl * _dx / maxSpeed;
    }

    public void Step(double dt)
    {
        FillGhostCells();
        ComputePrimitives();
        ReconstructAndEvolveFaces(dt);
        ComputeInterfaceFluxes();
        UpdateConserved(dt);
        Time += dt;
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
                Copy(last - Ghost + 1 + k, k);            // left ghosts ← right interior
                Copy(first + k, last + 1 + k);            // right ghosts ← left interior
            }

            return;
        }

        for (var k = 0; k < Ghost; k++)
        {
            var leftGhost = Ghost - 1 - k;                // adjacent first
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

        void Copy(int from, int to)
        {
            _rho[to] = _rho[from];
            _mom[to] = _mom[from];
            _ener[to] = _ener[from];
        }
    }

    private void ComputePrimitives()
    {
        for (var c = 0; c < _rho.Length; c++)
        {
            _wRho[c] = _rho[c];
            _wU[c] = _mom[c] / _rho[c];
            _wP[c] = _gas.Pressure(_rho[c], _mom[c], _ener[c]);
        }
    }

    private void ReconstructAndEvolveFaces(double dt)
    {
        var halfDtOverDx = 0.5 * dt / _dx;

        // Face states needed for every cell adjacent to an interior interface.
        for (var c = 1; c <= _n + 2; c++)
        {
            var sRho = SlopeLimiters.Limit(Limiter, _wRho[c] - _wRho[c - 1], _wRho[c + 1] - _wRho[c]);
            var sU = SlopeLimiters.Limit(Limiter, _wU[c] - _wU[c - 1], _wU[c + 1] - _wU[c]);
            var sP = SlopeLimiters.Limit(Limiter, _wP[c] - _wP[c - 1], _wP[c + 1] - _wP[c]);

            var lRho = _wRho[c] - 0.5 * sRho;
            var rRho = _wRho[c] + 0.5 * sRho;
            var lP = _wP[c] - 0.5 * sP;
            var rP = _wP[c] + 0.5 * sP;

            // Positivity guard: a reconstruction that goes non-physical falls
            // back to first order for this cell (near-vacuum robustness, 123 test).
            if (lRho <= 0 || rRho <= 0 || lP <= 0 || rP <= 0)
            {
                sRho = sU = sP = 0.0;
                lRho = rRho = _wRho[c];
                lP = rP = _wP[c];
            }

            var left = new PrimitiveState(lRho, _wU[c] - 0.5 * sU, lP);
            var right = new PrimitiveState(rRho, _wU[c] + 0.5 * sU, rP);

            // Hancock half-step in conservative variables.
            var (flRho, flMom, flEner) = EulerMath.Flux(left, _gas);
            var (frRho, frMom, frEner) = EulerMath.Flux(right, _gas);
            var dRho = halfDtOverDx * (flRho - frRho);
            var dMom = halfDtOverDx * (flMom - frMom);
            var dEner = halfDtOverDx * (flEner - frEner);

            EvolveFace(left, dRho, dMom, dEner, c, _faceLRho, _faceLU, _faceLP);
            EvolveFace(right, dRho, dMom, dEner, c, _faceRRho, _faceRU, _faceRP);
        }
    }

    private void EvolveFace(
        in PrimitiveState w, double dRho, double dMom, double dEner,
        int c, double[] outRho, double[] outU, double[] outP)
    {
        var rho = w.Rho + dRho;
        var mom = w.Rho * w.U + dMom;
        var ener = _gas.TotalEnergy(w.Rho, w.U, w.P) + dEner;
        var p = _gas.Pressure(rho, mom, ener);

        // Half-step positivity guard: keep the un-evolved face state instead.
        if (rho <= 0 || p <= 0)
        {
            outRho[c] = w.Rho;
            outU[c] = w.U;
            outP[c] = w.P;
            return;
        }

        outRho[c] = rho;
        outU[c] = mom / rho;
        outP[c] = p;
    }

    private void ComputeInterfaceFluxes()
    {
        // Interface j sits between cells c = j + 1 and c + 1, j = 0..n.
        for (var j = 0; j <= _n; j++)
        {
            var c = j + 1;
            var left = new PrimitiveState(_faceRRho[c], _faceRU[c], _faceRP[c]);
            var right = new PrimitiveState(_faceLRho[c + 1], _faceLU[c + 1], _faceLP[c + 1]);
            (_fluxRho[j], _fluxMom[j], _fluxEner[j]) = HllcFlux.Compute(left, right, _gas);
        }
    }

    private void UpdateConserved(double dt)
    {
        var dtOverDx = dt / _dx;
        for (var i = 0; i < _n; i++)
        {
            var c = i + Ghost;
            _rho[c] -= dtOverDx * (_fluxRho[i + 1] - _fluxRho[i]);
            _mom[c] -= dtOverDx * (_fluxMom[i + 1] - _fluxMom[i]);
            _ener[c] -= dtOverDx * (_fluxEner[i + 1] - _fluxEner[i]);
        }
    }
}
