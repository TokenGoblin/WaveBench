using FluentAssertions;
using WaveBench.Model.Units;
using Xunit;

namespace WaveBench.Core.Tests.Units;

public class SoundLevelTests
{
    [Theory]
    [InlineData("110 dB(C)", 110.0, SoundWeighting.C)]
    [InlineData("110 dBC", 110.0, SoundWeighting.C)]
    [InlineData("103dBc", 103.0, SoundWeighting.C)]
    [InlineData("95 dB(A)", 95.0, SoundWeighting.A)]
    [InlineData("95 dBA", 95.0, SoundWeighting.A)]
    [InlineData("88 dB", 88.0, SoundWeighting.Unweighted)]
    [InlineData("88.5 dB", 88.5, SoundWeighting.Unweighted)]
    public void Parses_level_and_weighting(string text, double expectedDb, SoundWeighting expectedWeighting)
    {
        var level = SoundLevel.Parse(text);
        level.Decibels.Should().Be(expectedDb);
        level.Weighting.Should().Be(expectedWeighting);
    }

    [Theory]
    [InlineData("110")]
    [InlineData("dB")]
    [InlineData("110 dB(B)")]
    [InlineData("110 sone")]
    public void Invalid_sound_levels_fail(string text) =>
        SoundLevel.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public void Formats_with_weighting_label()
    {
        SoundLevel.FromDecibels(110.0, SoundWeighting.C).ToString(1).Should().Be("110.0 dB(C)");
        SoundLevel.FromDecibels(95.0, SoundWeighting.A).ToString(0).Should().Be("95 dB(A)");
        SoundLevel.FromDecibels(88.0).ToString(1).Should().Be("88.0 dB");
    }

    [Fact]
    public void Comparing_same_weighting_works()
    {
        var a = SoundLevel.FromDecibels(103.0, SoundWeighting.C);
        var b = SoundLevel.FromDecibels(110.0, SoundWeighting.C);
        a.CompareTo(b).Should().BeNegative();
    }

    [Fact]
    public void Comparing_different_weightings_throws()
    {
        var dba = SoundLevel.FromDecibels(95.0, SoundWeighting.A);
        var dbc = SoundLevel.FromDecibels(103.0, SoundWeighting.C);
        var act = () => dba.CompareTo(dbc);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Round_trips_through_format_and_parse()
    {
        var original = SoundLevel.FromDecibels(109.3, SoundWeighting.C);
        var reparsed = SoundLevel.Parse(original.ToString(1));
        reparsed.Should().Be(SoundLevel.FromDecibels(109.3, SoundWeighting.C));
    }
}
