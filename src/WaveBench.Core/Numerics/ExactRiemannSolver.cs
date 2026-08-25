namespace WaveBench.Core.Numerics;

/// <summary>
/// Exact Riemann solver for the 1D Euler equations with a perfect gas.
/// Toro, "Riemann Solvers and Numerical Methods for Fluid Dynamics",
/// 3rd ed., ch. 4: Newton iteration on the pressure function with the
/// adaptive initial guess of §9.3, then self-similar sampling in ξ = x/t.
/// This is the verification reference for the §6.1 shock-tube tests — it is
/// never part of the production solve.
/// </summary>
public sealed class ExactRiemannSolver
{
    private readonly PerfectGas _gas;
    private readonly PrimitiveState _left;
    private readonly PrimitiveState _right;
    private readonly double _aL;
    private readonly double _aR;

    public ExactRiemannSolver(in PrimitiveState left, in PrimitiveState right, in PerfectGas gas)
    {
        _gas = gas;
        _left = left;
        _right = right;
        _aL = gas.SoundSpeed(left.Rho, left.P);
        _aR = gas.SoundSpeed(right.Rho, right.P);

        var g = gas.Gamma;
        // Pressure positivity (vacuum) condition, Toro eq. 4.40.
        if (2.0 / (g - 1.0) * (_aL + _aR) <= right.U - left.U)
        {
            throw new ArgumentException("Initial data generate vacuum; not supported.");
        }

        (PressureStar, VelocityStar) = SolveStarRegion();
    }

    public double PressureStar { get; }

    public double VelocityStar { get; }

    private (double PStar, double UStar) SolveStarRegion()
    {
        var p = InitialGuess();
        for (var iteration = 0; iteration < 100; iteration++)
        {
            var (fL, dfL) = PressureFunction(p, _left, _aL);
            var (fR, dfR) = PressureFunction(p, _right, _aR);
            var f = fL + fR + (_right.U - _left.U);
            var delta = f / (dfL + dfR);
            var pNew = Math.Max(p - delta, 1e-14);
            if (2.0 * Math.Abs(pNew - p) / (pNew + p) < 1e-12)
            {
                p = pNew;
                break;
            }

            p = pNew;
        }

        var (fLFinal, _) = PressureFunction(p, _left, _aL);
        var (fRFinal, _) = PressureFunction(p, _right, _aR);
        var u = 0.5 * (_left.U + _right.U) + 0.5 * (fRFinal - fLFinal);
        return (p, u);
    }

    private double InitialGuess()
    {
        var g = _gas.Gamma;
        var pPvrs = 0.5 * (_left.P + _right.P)
                    - 0.125 * (_right.U - _left.U) * (_left.Rho + _right.Rho) * (_aL + _aR);
        var pMin = Math.Min(_left.P, _right.P);
        var pMax = Math.Max(_left.P, _right.P);

        if (pMax / pMin <= 2.0 && pPvrs >= pMin && pPvrs <= pMax)
        {
            return Math.Max(1e-14, pPvrs);
        }

        if (pPvrs < pMin)
        {
            // Two-rarefaction approximation (Toro eq. 9.32).
            var z = (g - 1.0) / (2.0 * g);
            var numerator = _aL + _aR - 0.5 * (g - 1.0) * (_right.U - _left.U);
            var denominator = _aL / Math.Pow(_left.P, z) + _aR / Math.Pow(_right.P, z);
            return Math.Max(1e-14, Math.Pow(numerator / denominator, 1.0 / z));
        }

        // Two-shock approximation (Toro eq. 9.42).
        var p0 = Math.Max(1e-14, pPvrs);
        var gL = ShockG(p0, _left);
        var gR = ShockG(p0, _right);
        return Math.Max(1e-14, (gL * _left.P + gR * _right.P - (_right.U - _left.U)) / (gL + gR));
    }

    private double ShockG(double p, in PrimitiveState w)
    {
        var g = _gas.Gamma;
        var a = 2.0 / ((g + 1.0) * w.Rho);
        var b = (g - 1.0) / (g + 1.0) * w.P;
        return Math.Sqrt(a / (p + b));
    }

    private (double F, double Df) PressureFunction(double p, in PrimitiveState w, double a)
    {
        var g = _gas.Gamma;
        if (p > w.P)
        {
            // Shock branch (Toro eq. 4.6/4.7).
            var ak = 2.0 / ((g + 1.0) * w.Rho);
            var bk = (g - 1.0) / (g + 1.0) * w.P;
            var sqrt = Math.Sqrt(ak / (p + bk));
            var f = (p - w.P) * sqrt;
            var df = sqrt * (1.0 - 0.5 * (p - w.P) / (p + bk));
            return (f, df);
        }
        else
        {
            // Rarefaction branch (Toro eq. 4.6/4.7).
            var f = 2.0 * a / (g - 1.0) * (Math.Pow(p / w.P, (g - 1.0) / (2.0 * g)) - 1.0);
            var df = 1.0 / (w.Rho * a) * Math.Pow(p / w.P, -(g + 1.0) / (2.0 * g));
            return (f, df);
        }
    }

    /// <summary>Self-similar solution at ξ = x/t (Toro §4.5).</summary>
    public PrimitiveState Sample(double xi)
    {
        var g = _gas.Gamma;
        var gm = (g - 1.0) / (g + 1.0);

        if (xi <= VelocityStar)
        {
            // Left of the contact.
            if (PressureStar > _left.P)
            {
                // Left shock.
                var sL = _left.U - _aL * Math.Sqrt((g + 1.0) / (2.0 * g) * PressureStar / _left.P
                                                   + (g - 1.0) / (2.0 * g));
                if (xi <= sL)
                {
                    return _left;
                }

                var rho = _left.Rho * (PressureStar / _left.P + gm) / (gm * PressureStar / _left.P + 1.0);
                return new PrimitiveState(rho, VelocityStar, PressureStar);
            }
            else
            {
                // Left rarefaction.
                var headL = _left.U - _aL;
                if (xi <= headL)
                {
                    return _left;
                }

                var aStarL = _aL * Math.Pow(PressureStar / _left.P, (g - 1.0) / (2.0 * g));
                var tailL = VelocityStar - aStarL;
                if (xi >= tailL)
                {
                    var rhoStar = _left.Rho * Math.Pow(PressureStar / _left.P, 1.0 / g);
                    return new PrimitiveState(rhoStar, VelocityStar, PressureStar);
                }

                // Inside the fan.
                var factor = 2.0 / (g + 1.0) + gm / _aL * (_left.U - xi);
                var u = 2.0 / (g + 1.0) * (_aL + 0.5 * (g - 1.0) * _left.U + xi);
                var rho = _left.Rho * Math.Pow(factor, 2.0 / (g - 1.0));
                var p = _left.P * Math.Pow(factor, 2.0 * g / (g - 1.0));
                return new PrimitiveState(rho, u, p);
            }
        }
        else
        {
            // Right of the contact (mirror).
            if (PressureStar > _right.P)
            {
                var sR = _right.U + _aR * Math.Sqrt((g + 1.0) / (2.0 * g) * PressureStar / _right.P
                                                    + (g - 1.0) / (2.0 * g));
                if (xi >= sR)
                {
                    return _right;
                }

                var rho = _right.Rho * (PressureStar / _right.P + gm) / (gm * PressureStar / _right.P + 1.0);
                return new PrimitiveState(rho, VelocityStar, PressureStar);
            }
            else
            {
                var headR = _right.U + _aR;
                if (xi >= headR)
                {
                    return _right;
                }

                var aStarR = _aR * Math.Pow(PressureStar / _right.P, (g - 1.0) / (2.0 * g));
                var tailR = VelocityStar + aStarR;
                if (xi <= tailR)
                {
                    var rhoStar = _right.Rho * Math.Pow(PressureStar / _right.P, 1.0 / g);
                    return new PrimitiveState(rhoStar, VelocityStar, PressureStar);
                }

                var factor = 2.0 / (g + 1.0) - gm / _aR * (_right.U - xi);
                var u = 2.0 / (g + 1.0) * (-_aR + 0.5 * (g - 1.0) * _right.U + xi);
                var rho = _right.Rho * Math.Pow(factor, 2.0 / (g - 1.0));
                var p = _right.P * Math.Pow(factor, 2.0 * g / (g - 1.0));
                return new PrimitiveState(rho, u, p);
            }
        }
    }
}
