using WaveBench.Core.Components;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;

namespace WaveBench.Core.EngineModel;

/// <summary>
/// Poppet-valve boundary between a cylinder and a duct end (plan §2.6),
/// solved JOINTLY with the adjacent cell's characteristics, not
/// sequentially: the duct-face state is constrained to the interior's
/// outgoing Riemann invariant and isentrope, and the face pressure is
/// iterated until the face mass flux equals the orifice mass flow. Choking
/// and both flow directions (including intake reversion) are handled.
/// </summary>
public sealed class ValveConnection
{
    private readonly Cylinder _cylinder;
    private readonly DuctSolver _duct;
    private readonly bool _ductLeftEnd;
    private readonly CamProfile _cam;
    private readonly ValveGeometry _valve;
    private readonly ValveCdMap _cdMap;
    private readonly double[] _yCylinder;
    private readonly double[] _yDuct;

    public ValveConnection(
        Cylinder cylinder, DuctSolver duct, bool ductLeftEnd,
        CamProfile cam, ValveGeometry valve, ValveCdMap? cdMap = null)
    {
        _cylinder = cylinder;
        _duct = duct;
        _ductLeftEnd = ductLeftEnd;
        _cam = cam;
        _valve = valve;
        _cdMap = cdMap ?? ValveCdMap.Generic;
        _yCylinder = new double[duct.Gas.SpeciesCount];
        _yDuct = new double[duct.Gas.SpeciesCount];
    }

    /// <summary>Mass flow of the last update, positive into the cylinder, kg/s.</summary>
    public double MassFlow { get; private set; }

    public double CurrentLift { get; private set; }

    private int AdjacentCell => _ductLeftEnd ? 0 : _duct.CellCount - 1;

    private double FaceArea => _ductLeftEnd ? _duct.Geometry.FaceArea[0] : _duct.Geometry.FaceArea[^1];

    /// <summary>
    /// Solve the valve for the coming step and impose the duct end flux and
    /// the cylinder port flow.
    /// </summary>
    public void Update(double dt, double engineAngleDeg)
    {
        var lift = _cam.Lift(_cylinder.LocalAngle(engineAngleDeg));
        CurrentLift = lift;
        var area = _valve.EffectiveArea(lift);

        if (area < 1e-9)
        {
            MassFlow = 0.0;
            CloseValve();
            return;
        }

        var s = _duct.GetState(AdjacentCell);
        var w = _duct.GetPrimitive(AdjacentCell);
        var gamma = s.Gamma;
        var g1 = gamma - 1.0;
        var r = s.P / (w.Rho * s.T);
        var sign = _ductLeftEnd ? 1.0 : -1.0;        // +x for flow into the duct at this end
        var uIn = s.U * sign;                         // interior velocity, into-duct positive
        var rMinus = uIn - 2.0 * s.SoundSpeed / g1;   // outgoing invariant at this end

        var pCyl = _cylinder.Pressure;
        var liftRatio = lift / _valve.HeadDiameter;

        // The face state on the invariant/isentrope as a function of face pressure.
        (double Rho, double USigned, double A) Face(double pFace)
        {
            var rhoF = w.Rho * Math.Pow(pFace / s.P, 1.0 / gamma);
            var aF = Math.Sqrt(gamma * pFace / rhoF);
            var uF = rMinus + 2.0 * aF / g1;
            return (rhoF, uF, aF);
        }

        // Residual: face mass flux (into duct positive) minus orifice flow.
        double Residual(double pFace)
        {
            var (rhoF, uF, aF) = Face(pFace);
            var faceFlux = rhoF * uF * FaceArea;

            double orifice;
            if (pCyl >= pFace)
            {
                // Cylinder → duct (exhaust blowdown / intake reversion source).
                var cd = _cdMap.Cd(liftRatio, pFace / pCyl);
                orifice = CompressibleOrifice.MassFlow(cd, area, pCyl, _cylinder.Temperature, pFace, gamma, r);
            }
            else
            {
                // Duct → cylinder: upstream stagnation from the face state.
                var mach = Math.Abs(uF) / aF;
                var p0 = pFace * Math.Pow(1 + 0.5 * g1 * mach * mach, gamma / g1);
                var t0 = pFace / (rhoF * r) * (1 + 0.5 * g1 * mach * mach);
                var cd = _cdMap.Cd(liftRatio, pCyl / p0);
                orifice = -CompressibleOrifice.MassFlow(cd, area, p0, t0, pCyl, gamma, r);
            }

            return faceFlux - orifice;
        }

        // Bracket and bisect on face pressure. The face flux increases with
        // decreasing pressure (larger u on the invariant); the orifice term
        // moves the other way, so the residual is monotone.
        var pLo = 0.2 * Math.Min(s.P, pCyl);
        var pHi = 3.0 * Math.Max(s.P, pCyl);
        var fLo = Residual(pLo);
        var fHi = Residual(pHi);
        double pFaceSolved;
        if (fLo * fHi > 0)
        {
            // Degenerate bracket (deep choke or near-closed): fall back to the
            // interior pressure, which keeps the coupling consistent.
            pFaceSolved = s.P;
        }
        else
        {
            for (var i = 0; i < 60; i++)
            {
                var mid = 0.5 * (pLo + pHi);
                if (Residual(mid) * fLo > 0)
                {
                    pLo = mid;
                    fLo = Residual(pLo);
                }
                else
                {
                    pHi = mid;
                }
            }

            pFaceSolved = 0.5 * (pLo + pHi);
        }

        var (rhoFace, uFace, aFace) = Face(pFaceSolved);
        var mDotIntoDuct = rhoFace * uFace * FaceArea;
        MassFlow = -mDotIntoDuct;

        // Impose the duct end flux (+x sense).
        var fRho = sign * rhoFace * uFace;
        var fMom = pFaceSolved + rhoFace * uFace * uFace;
        double h;
        double[]? yIn = null;
        if (mDotIntoDuct > 0)
        {
            // Cylinder feeds the duct.
            h = _cylinder.SpecificEnthalpy;
            if (_yCylinder.Length > 0)
            {
                _cylinder.CopyMassFractions(_yCylinder);
                yIn = _yCylinder;
            }
        }
        else
        {
            var cp = gamma * r / g1;
            h = cp * s.T + 0.5 * s.U * s.U;
        }

        SetDuctFluxRaw(fRho, fMom, fRho * h, yIn);

        // Cylinder side.
        if (MassFlow >= 0)
        {
            // Duct → cylinder: duct-face enthalpy and composition.
            for (var k = 0; k < _yDuct.Length; k++)
            {
                _yDuct[k] = _duct.GetMassFraction(k, AdjacentCell);
            }

            var cp = gamma * r / g1;
            var hFace = cp * (pFaceSolved / (rhoFace * r)) + 0.5 * uFace * uFace;
            _cylinder.QueueFlow(MassFlow * dt, hFace, _yDuct);
        }
        else
        {
            _cylinder.QueueFlow(MassFlow * dt, 0.0, default);
        }
    }

    private void CloseValve()
    {
        // Closed valve: solid wall at the end face — pressure-only momentum flux.
        var w = _duct.GetPrimitive(AdjacentCell);
        SetDuctFluxRaw(0.0, w.P, 0.0, null);
    }

    private void SetDuctFluxRaw(double fRho, double fMom, double fEner, double[]? yIn)
    {
        if (_ductLeftEnd)
        {
            _duct.LeftFluxOverride = (fRho, fMom, fEner);
            _duct.LeftFluxComposition = yIn;
        }
        else
        {
            _duct.RightFluxOverride = (fRho, fMom, fEner);
            _duct.RightFluxComposition = yIn;
        }
    }
}
