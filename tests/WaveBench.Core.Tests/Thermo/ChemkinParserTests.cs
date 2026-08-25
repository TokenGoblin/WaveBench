using FluentAssertions;
using WaveBench.Core.Thermo;
using Xunit;

namespace WaveBench.Core.Tests.Thermo;

public class ChemkinParserTests
{
    // GRI-style block: integer element counts, explicit Tmid.
    private const string GriStyle =
        "N2                121286N   2               G   300.000  5000.000  1000.000    1\n" +
        " 0.02926640E+02 0.14879768E-02-0.05684760E-05 0.10097038E-09-0.06753351E-13    2\n" +
        "-0.09227977E+04 0.05980528E+02 0.03298677E+02 0.14082404E-02-0.03963222E-04    3\n" +
        " 0.05641515E-07-0.02444854E-10-0.10208999E+04 0.03950372E+02                   4\n";

    // Burcat-style block: decimal element counts, Tmid "1000.", h298/R appended on line 4.
    private const string BurcatStyle =
        "C2H5OH            L 8/88C   2H   6O   1    0G   200.000  6000.000 1000.        1\n" +
        " 0.65624365E+01 0.15204222E-01-0.53896795E-05 0.86225011E-09-0.51289787E-13    2\n" +
        "-0.31525621E+05-0.94730202E+01 0.48586957E+01-0.37401726E-02 0.69555378E-04    3\n" +
        "-0.88654796E-07 0.35168835E-10-0.29996132E+05 0.48018545E+01-0.28257829E+05    4\n";

    [Fact]
    public void Parses_gri_style_block()
    {
        var species = ChemkinThermoParser.Parse(new StringReader(GriStyle)).Single();
        species.Name.Should().Be("N2");
        species.Elements["N"].Should().Be(2);
        species.TLow.Should().Be(300.0);
        species.TMid.Should().Be(1000.0);
        species.THigh.Should().Be(5000.0);
        species.MolarMass.Should().BeApproximately(28.014, 0.001);
    }

    [Fact]
    public void Parses_burcat_style_block_ignoring_trailing_h298()
    {
        var species = ChemkinThermoParser.Parse(new StringReader(BurcatStyle)).Single();
        species.Name.Should().Be("C2H5OH");
        species.Elements["C"].Should().Be(2);
        species.Elements["H"].Should().Be(6);
        species.Elements["O"].Should().Be(1);
        species.TMid.Should().Be(1000.0);
        // Ethanol vapour cp at 298 K ≈ 65.4 J/(mol·K).
        (species.MolarCp(298.15) / 1000.0).Should().BeApproximately(65.4, 1.5);
    }

    [Fact]
    public void Parses_decimal_element_counts()
    {
        const string decimalCounts =
            "C7H8              g 1/93C  7.H  8.   0.   0.G   200.000  6000.000 1000.        1\n" +
            " 1.29393610E+01 2.66922277E-02-9.68422041E-06 1.57392386E-09-9.46671699E-14    2\n" +
            "-6.76971149E+02-4.67249759E+01 1.61200102E+00 2.11179855E-02 8.53239986E-05    3\n" +
            "-1.32568501E-07 5.59411406E-11 4.09654820E+03 2.02969771E+01 6.03402967E+03    4\n";
        var species = ChemkinThermoParser.Parse(new StringReader(decimalCounts)).Single();
        species.Elements["C"].Should().Be(7);
        species.Elements["H"].Should().Be(8);
        species.MolarMass.Should().BeApproximately(92.141, 0.001);
    }

    [Fact]
    public void Comments_and_keywords_are_skipped()
    {
        var text = "! comment\nTHERMO ALL\n" + GriStyle + "END\n";
        ChemkinThermoParser.Parse(new StringReader(text)).Should().HaveCount(1);
    }

    [Fact]
    public void Truncated_block_throws()
    {
        var lines = GriStyle.Split('\n')[..2];
        var act = () => ChemkinThermoParser.Parse(new StringReader(string.Join('\n', lines)));
        act.Should().Throw<FormatException>();
    }
}
