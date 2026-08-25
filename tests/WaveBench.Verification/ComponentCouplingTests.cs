using FluentAssertions;
using WaveBench.Core.Components;
using WaveBench.Core.Numerics;
using WaveBench.Core.Solver;
using WaveBench.Core.Thermo;
using Xunit;

namespace WaveBench.Verification;

/// <summary>
/// Phase 4 coupled-component verification: plenum blowdown against the exact
/// 0D ODE, junction mass conservation and behaviour, injector conservation.
/// </summary>
public class ComponentCouplingTests
{
    private static readonly PerfectGas Gas = new(1.4, 287.05);

    [Fact]
    public void Plenum_blowdown_matches_the_reference_ode()
    {
        // 5 L plenum at 3 bar discharging through a 1 cm², Cd 0.8 orifice to
        // 1 bar ambient. Reference: adiabatic 0D blowdown ODE integrated with
        // fine steps and the same orifice law.
        var gasModel = new PerfectGasModel(Gas);
        var plenum = new PlenumVolume(gasModel, 0.005, 3.0e5, 300.0);
        var ambient = new AmbientEndpoint(1.0e5, 300.0, Gas.SpecificGasConstant, Gas.Gamma);
        var orifice = new OrificeConnector(new PlenumEndpoint(plenum, gasModel), ambient)
        {
            EffectiveArea = 1e-4,
            DischargeCoefficientAtoB = 0.8,
            DischargeCoefficientBtoA = 0.8,
        };

        const double tEnd = 0.05;
        const double dt = 1e-5;
        for (var t = 0.0; t < tEnd; t += dt)
        {
            orifice.Update(dt);
            plenum.Commit();
        }

        // Reference ODE: adiabatic vessel, dm = −ṁ dt, dE = −ṁ·h dt.
        double v = 0.005, m = 3.0e5 * v / (Gas.SpecificGasConstant * 300.0);
        var e = m * Gas.SpecificGasConstant / (Gas.Gamma - 1.0) * 300.0;
        const double dtRef = 1e-6;
        for (var t = 0.0; t < tEnd; t += dtRef)
        {
            var temp = e / (m * Gas.SpecificGasConstant / (Gas.Gamma - 1.0));
            var p = m / v * Gas.SpecificGasConstant * temp;
            var mDot = CompressibleOrifice.MassFlow(0.8, 1e-4, p, temp, 1.0e5, Gas.Gamma, Gas.SpecificGasConstant);
            var h = Gas.Gamma * Gas.SpecificGasConstant / (Gas.Gamma - 1.0) * temp;
            m -= mDot * dtRef;
            e -= mDot * h * dtRef;
        }

        var tRef = e / (m * Gas.SpecificGasConstant / (Gas.Gamma - 1.0));
        var pRef = m / v * Gas.SpecificGasConstant * tRef;

        plenum.Pressure.Should().BeApproximately(pRef, pRef * 0.005,
            $"plenum energy/mass balance (reference {pRef:F0} Pa, plenum {plenum.Pressure:F0} Pa)");
        plenum.Temperature.Should().BeApproximately(tRef, 2.0, "adiabatic expansion cools the vessel");
        plenum.Temperature.Should().BeLessThan(295.0);
    }

    [Fact]
    public void Constant_pressure_junction_conserves_mass_and_splits_a_symmetric_flow_evenly()
    {
        // One 50 mm feed duct from a 1.15 bar reservoir into two identical
        // 35 mm branches discharging to 1.0 bar: by symmetry the branches
        // carry identical flow, and junction in ≈ out.
        var gasModel = new PerfectGasModel(Gas);
        var feed = MakeDuct(gasModel, 0.050);
        var branchA = MakeDuct(gasModel, 0.035);
        var branchB = MakeDuct(gasModel, 0.035);

        feed.LeftBoundary = BoundaryKind.External;
        feed.LeftEnd = new ReservoirBoundary { StagnationPressure = 1.15e5, StagnationTemperature = 300.0 };
        foreach (var b in new[] { branchA, branchB })
        {
            b.RightBoundary = BoundaryKind.External;
            b.RightEnd = new ReservoirBoundary { StagnationPressure = 1.0e5, StagnationTemperature = 300.0 };
        }

        var junction = new Junction(gasModel);
        junction.Connect(feed, leftEnd: false);
        junction.Connect(branchA, leftEnd: true);
        junction.Connect(branchB, leftEnd: true);

        RunNetwork(0.05, junction, feed, branchA, branchB);

        var mIn = MassFlowAt(feed, feed.CellCount - 5);
        var mA = MassFlowAt(branchA, 5);
        var mB = MassFlowAt(branchB, 5);

        mIn.Should().BeGreaterThan(0.01, "the feed must actually flow");
        mA.Should().BeApproximately(mB, Math.Abs(mA) * 0.001, "symmetric branches split evenly");
        (mA + mB).Should().BeApproximately(mIn, mIn * 0.01, "junction conserves mass");
    }

    [Fact]
    public void Tee_losses_apply_the_published_coefficient_in_a_combining_junction()
    {
        // Combining tee: straight and 90° side legs both fed at 1.06 bar,
        // combined leg discharging to 1.0 bar. (A dividing tee between pure
        // frictionless reservoirs is inherently loss-dominated — the ejector
        // regime — so combining is the well-posed integration check; the
        // coefficient values themselves are unit-tested against published
        // anchors in ComponentTests.)
        var gasModel = new PerfectGasModel(Gas);

        (double Combined, double SideEndP0, double NodeP) Run(JunctionModel model)
        {
            var straight = MakeDuct(gasModel, 0.050);
            var side = MakeDuct(gasModel, 0.035);
            var combined = MakeDuct(gasModel, 0.050);

            foreach (var d in new[] { straight, side })
            {
                d.LeftBoundary = BoundaryKind.External;
                d.LeftEnd = new ReservoirBoundary { StagnationPressure = 1.06e5, StagnationTemperature = 300.0 };
            }

            combined.RightBoundary = BoundaryKind.External;
            combined.RightEnd = new ReservoirBoundary { StagnationPressure = 1.0e5, StagnationTemperature = 300.0 };

            var junction = new Junction(gasModel) { Model = model };
            junction.Connect(combined, leftEnd: true);
            junction.Connect(straight, leftEnd: false);
            junction.Connect(side, leftEnd: false, isSideBranch: true);

            RunNetwork(0.05, junction, combined, straight, side);

            var sideEnd = side.GetState(side.CellCount - 1);
            var sideRho = side.GetPrimitive(side.CellCount - 1).Rho;
            var mach = Math.Abs(sideEnd.U) / sideEnd.SoundSpeed;
            var p0Side = sideEnd.P * Math.Pow(1 + 0.2 * mach * mach, 3.5);
            return (MassFlowAt(combined, 50), p0Side, junction.Pressure);
        }

        var lossless = Run(JunctionModel.ConstantPressure);
        var lossy = Run(JunctionModel.TeeWithLosses);

        lossless.Combined.Should().BeGreaterThan(0.05, "the junction must flow");
        lossy.Combined.Should().BeLessThan(lossless.Combined,
            "combining losses reduce the delivered flow");
        lossy.Combined.Should().BeGreaterThan(lossless.Combined * 0.5,
            "combining-tee losses are moderate (ξ ≈ 0.4–0.6 of one dynamic head)");

        // The side leg's stagnation pressure must exceed the node pressure —
        // it is fighting its published pair loss.
        (lossy.SideEndP0 - lossy.NodeP).Should().BeGreaterThan(
            (lossless.SideEndP0 - lossless.NodeP) + 100.0,
            "the applied side-branch loss appears as extra stagnation-pressure demand");
    }

    [Fact]
    public void Injector_adds_exactly_the_metered_fuel_mass()
    {
        var db = SpeciesDatabase.Default;
        var gasModel = new MultiSpeciesGasModel(db, ["N2", "O2", "AR", "CO2", "CH3OH"]);
        var air = gasModel.MassFractionsOf(GasComposition.DryAir(db));

        var duct = new DuctSolver(DuctGeometry.Uniform(0.5, 100, 0.04), gasModel)
        {
            LeftBoundary = BoundaryKind.Periodic,
            RightBoundary = BoundaryKind.Periodic,
        };
        for (var i = 0; i < 100; i++)
        {
            duct.SetState(i, new PrimitiveState(1.1, 20.0, 101_325.0), air);
        }

        var fuelIndex = 4; // CH3OH
        duct.MassSources.Add(new DuctMassSource
        {
            Cell = 50,
            SpeciesIndex = fuelIndex,
            MassRate = 2e-3,
            Temperature = 320.0,
        });

        var fuelBefore = duct.SpeciesTotalMass(fuelIndex);
        var totalBefore = duct.ConservedTotals().Mass;

        const double tEnd = 0.02;
        duct.Advance(tEnd);

        var expected = 2e-3 * tEnd;
        (duct.SpeciesTotalMass(fuelIndex) - fuelBefore).Should().BeApproximately(expected, expected * 1e-6,
            "injected fuel mass is exactly metered");
        (duct.ConservedTotals().Mass - totalBefore).Should().BeApproximately(expected, expected * 1e-6);

        // Composition downstream of the injector contains fuel vapour.
        duct.GetMassFraction(fuelIndex, 60).Should().BeGreaterThan(1e-4);
    }

    private static DuctSolver MakeDuct(PerfectGasModel gasModel, double diameter)
    {
        var duct = new DuctSolver(DuctGeometry.Uniform(0.5, 100, diameter), gasModel);
        for (var i = 0; i < 100; i++)
        {
            duct.SetState(i, new PrimitiveState(1.0e5 / (287.05 * 300.0), 0.0, 1.0e5));
        }

        return duct;
    }

    private static void RunNetwork(double tEnd, Junction junction, params DuctSolver[] ducts)
    {
        var t = 0.0;
        while (t < tEnd)
        {
            var dt = ducts.Min(d => d.StableTimestep());
            junction.Update();
            foreach (var d in ducts)
            {
                d.Step(dt);
            }

            t += dt;
        }
    }

    private static double MassFlowAt(DuctSolver duct, int cell)
    {
        var w = duct.GetPrimitive(cell);
        return w.Rho * w.U * duct.Geometry.CellArea[cell];
    }
}
