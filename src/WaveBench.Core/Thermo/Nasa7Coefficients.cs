namespace WaveBench.Core.Thermo;

/// <summary>
/// One temperature range of a NASA 7-coefficient polynomial
/// (Gordon &amp; McBride form, as used in CHEMKIN thermo files):
///   cp/R  = a1 + a2·T + a3·T² + a4·T³ + a5·T⁴
///   h/RT  = a1 + a2/2·T + a3/3·T² + a4/4·T³ + a5/5·T⁴ + a6/T
///   s/R   = a1·ln T + a2·T + a3/2·T² + a4/3·T³ + a5/4·T⁴ + a7
/// where h includes the enthalpy of formation at 298.15 K.
/// </summary>
public readonly record struct Nasa7Coefficients(
    double A1, double A2, double A3, double A4, double A5, double A6, double A7)
{
    public double CpOverR(double t) => A1 + t * (A2 + t * (A3 + t * (A4 + t * A5)));

    public double HOverRT(double t) =>
        A1 + t * (A2 / 2.0 + t * (A3 / 3.0 + t * (A4 / 4.0 + t * A5 / 5.0))) + A6 / t;

    public double SOverR(double t) =>
        A1 * Math.Log(t) + t * (A2 + t * (A3 / 2.0 + t * (A4 / 3.0 + t * A5 / 4.0))) + A7;
}
