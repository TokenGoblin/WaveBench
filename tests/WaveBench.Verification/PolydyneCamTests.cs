using FluentAssertions;
using WaveBench.Core.EngineModel;
using Xunit;
using Xunit.Abstractions;

namespace WaveBench.Verification;

/// <summary>
/// Phase 5 §2.5 verification of the polydyne cam generator.
///
/// A polydyne is defined by its boundary conditions, so the boundary
/// conditions are the test — lift, velocity, acceleration and jerk must all
/// vanish at the seat, exactly, not approximately. That is the whole reason
/// the family exists and the thing the harmonic profile fails.
/// </summary>
public class PolydyneCamTests(ITestOutputHelper output)
{
    /// <summary>Central-difference derivative of the normalised lift.</summary>
    private static double Derivative(Func<double, double> f, double x, int order, double h = 1e-3)
    {
        return order switch
        {
            1 => (f(x + h) - f(x - h)) / (2 * h),
            2 => (f(x + h) - (2 * f(x)) + f(x - h)) / (h * h),
            3 => (f(x + (2 * h)) - (2 * f(x + h)) + (2 * f(x - h)) - f(x - (2 * h))) / (2 * h * h * h),
            _ => f(x),
        };
    }

    [Theory]
    [InlineData(8, 10, 12)]
    [InlineData(6, 8, 10)]
    [InlineData(4, 6, 8)]
    [InlineData(8, 12, 16)]
    public void Gate_lift_velocity_acceleration_and_jerk_all_vanish_at_the_seat(int p, int q, int r)
    {
        // Checked against the EXACT derivatives, not finite differences. All
        // four quantities vanish together at the seat, so a difference stencil
        // there measures its own truncation error against the first surviving
        // derivative — it reported ~3e-5 for a quantity that is identically
        // zero, which would have meant choosing a tolerance to hide the
        // stencil rather than testing the cam.
        var c = CamProfile.PolydyneCoefficients(p, q, r);

        CamProfile.PolydyneLift(1.0, p, q, r, c).Should().BeApproximately(0.0, 1e-12, "the valve is seated");
        for (var order = 1; order <= 3; order++)
        {
            var name = order switch { 1 => "velocity", 2 => "acceleration", _ => "jerk" };
            CamProfile.PolydyneDerivative(1.0, order, p, q, r, c)
                .Should().BeApproximately(0.0, 1e-9, $"the follower arrives with zero {name}");
        }

        output.WriteLine($"{p}-{q}-{r}: C = [{string.Join(", ", c.Select(v => v.ToString("F4")))}]");
    }

    [Fact]
    public void Gate_the_2_4_6_8_family_reproduces_the_closed_form_exactly()
    {
        // The strongest anchor available, and it turned up by accident: with
        // exponents 2-4-6-8 the solver returns C = [−4, 6, −4, 1], which are
        // the binomial coefficients of (1 − x²)⁴. That function satisfies the
        // seating conditions by inspection — it has a fourth-order zero at
        // x = ±1 — so the linear solve is being checked against an independent
        // closed form rather than against itself.
        var c = CamProfile.PolydyneCoefficients(4, 6, 8);
        c.Should().HaveCount(4);
        c[0].Should().BeApproximately(-4.0, 1e-12);
        c[1].Should().BeApproximately(6.0, 1e-12);
        c[2].Should().BeApproximately(-4.0, 1e-12);
        c[3].Should().BeApproximately(1.0, 1e-12);

        for (var x = -1.0; x <= 1.0; x += 0.05)
        {
            var closedForm = Math.Pow(1.0 - (x * x), 4);
            CamProfile.PolydyneLift(x, 4, 6, 8, c).Should().BeApproximately(closedForm, 1e-12);
        }

        output.WriteLine("2-4-6-8 polydyne == (1 − x²)⁴ to 1e-12 across the whole flank");
    }

    [Fact]
    public void Gate_the_nose_is_unit_lift_at_zero_velocity()
    {
        CamProfile.PolydyneLift(0.0).Should().BeApproximately(1.0, 1e-12, "the nose is peak lift by construction");
        CamProfile.PolydyneDerivative(0.0, 1).Should()
            .BeApproximately(0.0, 1e-12, "and the follower is stationary there");
        CamProfile.PolydyneDerivative(0.0, 2).Should()
            .BeLessThan(0.0, "the follower decelerates over the nose");
    }

    [Fact]
    public void The_exact_derivatives_agree_with_finite_differences_away_from_the_seat()
    {
        // Guards the analytic derivative itself. Away from x = ±1 nothing is
        // cancelling, so a central difference is a fair independent check.
        foreach (var x in new[] { 0.2, 0.4, 0.6, 0.8 })
        {
            double Y(double t) => CamProfile.PolydyneLift(t);
            CamProfile.PolydyneDerivative(x, 1).Should().BeApproximately(Derivative(Y, x, 1), 1e-5);
            CamProfile.PolydyneDerivative(x, 2).Should().BeApproximately(Derivative(Y, x, 2), 1e-3);
        }
    }

    [Fact]
    public void Gate_the_harmonic_profile_slams_the_seat_where_the_polydyne_does_not()
    {
        // The contrast that justifies the whole generator. A raised cosine
        // reaches the seat with finite acceleration, so acceleration steps to
        // zero and jerk is unbounded — which is what makes a follower bounce.
        double Cosine(double x) => 0.5 * (1.0 + Math.Cos(Math.PI * x));   // 1 at nose, 0 at seat
        double Poly(double x) => CamProfile.PolydyneLift(x);

        var cosineSeatAccel = Math.Abs(Derivative(Cosine, 1.0, 2));
        var polySeatAccel = Math.Abs(Derivative(Poly, 1.0, 2));

        output.WriteLine($"seating acceleration (normalised): cosine {cosineSeatAccel:F4}, polydyne {polySeatAccel:E2}");
        cosineSeatAccel.Should().BeGreaterThan(4.0, "½π² ≈ 4.93 — the cosine hits the seat hard");
        polySeatAccel.Should().BeLessThan(cosineSeatAccel * 1e-4, "the polydyne arrives with nothing left");
    }

    [Fact]
    public void Gate_lift_stays_within_the_event_and_never_goes_negative()
    {
        var cam = CamProfile.Polydyne(openDeg: 340.0, closeDeg: 590.0, maxLift: 9.5e-3);

        cam.MaxLift.Should().BeApproximately(9.5e-3, 1e-5);
        cam.IsGeneric.Should().BeTrue("a generated profile is never the user's measured cam");

        // Closed everywhere outside the event.
        foreach (var angle in new[] { 0.0, 100.0, 339.0, 591.0, 700.0 })
        {
            cam.Lift(angle).Should().BeApproximately(0.0, 1e-9, $"the valve is shut at {angle:F0}°");
        }

        // Open, positive and single-peaked inside it.
        for (var angle = 341.0; angle < 589.0; angle += 1.0)
        {
            cam.Lift(angle).Should().BeGreaterThan(0.0).And.BeLessThanOrEqualTo(9.5e-3 + 1e-9);
        }

        // Peak at the centre of the event.
        var peakAngle = 340.0;
        var peak = 0.0;
        for (var angle = 340.0; angle <= 590.0; angle += 0.25)
        {
            if (cam.Lift(angle) > peak)
            {
                peak = cam.Lift(angle);
                peakAngle = angle;
            }
        }

        output.WriteLine($"peak {peak * 1000:F3} mm at {peakAngle:F1}° (event centre 465°)");
        peakAngle.Should().BeApproximately(465.0, 1.0);
    }

    [Fact]
    public void The_polydyne_opens_comparable_area_to_a_cosine_of_the_same_duration()
    {
        // Not a claim that it flows better — it does not. Measured, the
        // 2-8-10-12 polydyne encloses 99.5% of the cosine's lift-area at the
        // same peak and duration, so its advantage is entirely kinematic (see
        // the seating test), not a breathing gain. This pins that it is a
        // sane cam shape rather than a spike, which is what the comparison is
        // actually worth.
        const double open = 340.0, close = 590.0, lift = 9.5e-3;

        double Area(CamProfile cam)
        {
            var sum = 0.0;
            for (var a = open; a <= close; a += 0.1)
            {
                sum += cam.Lift(a) * 0.1;
            }

            return sum;
        }

        var polyArea = Area(CamProfile.Polydyne(open, close, lift));
        var cosineArea = Area(CamProfile.Harmonic(open, close, lift));

        output.WriteLine($"lift-area: polydyne {polyArea * 1e3:F3}, cosine {cosineArea * 1e3:F3} mm·deg " +
                         $"({100.0 * polyArea / cosineArea:F1}%)");
        (polyArea / cosineArea).Should().BeInRange(0.9, 1.1);
    }

    [Fact]
    public void Nonsense_exponents_are_rejected_rather_than_silently_producing_a_bad_cam()
    {
        var tooLow = () => CamProfile.PolydyneCoefficients(3, 10, 12);
        tooLow.Should().Throw<ArgumentException>("x³ makes the jerk condition degenerate");

        var notAscending = () => CamProfile.PolydyneCoefficients(10, 8, 12);
        notAscending.Should().Throw<ArgumentException>();

        var duplicated = () => CamProfile.PolydyneCoefficients(8, 8, 12);
        duplicated.Should().Throw<ArgumentException>("repeated exponents give a singular system");
    }

    [Fact]
    public void The_profile_is_symmetric_about_the_nose()
    {
        var cam = CamProfile.Polydyne(340.0, 590.0, 9.5e-3);
        for (var offset = 1.0; offset <= 120.0; offset += 1.0)
        {
            cam.Lift(465.0 - offset).Should().BeApproximately(cam.Lift(465.0 + offset), 1e-9);
        }
    }
}
