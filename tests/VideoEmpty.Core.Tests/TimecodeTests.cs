using VideoEmpty.Core.Model;
using Xunit;

namespace VideoEmpty.Core.Tests;

public class TimecodeTests
{
    [Theory]
    [InlineData("10:12", 10 * 60_000 + 12_000)]
    [InlineData("4:01:02", 4 * 3_600_000 + 1 * 60_000 + 2_000)]
    [InlineData("5:01:02", 5 * 3_600_000 + 1 * 60_000 + 2_000)]
    [InlineData("0:00", 0)]
    [InlineData("45", 45_000)]
    [InlineData("1.5", 1_500)]
    [InlineData("00:00:10.500", 10_500)]
    [InlineData("00:00:10,500", 10_500)]
    [InlineData(" 1:02:03 ", 3_723_000)]
    [InlineData("-10:12", -(10 * 60_000 + 12_000))]
    [InlineData("+10:12", 10 * 60_000 + 12_000)]
    public void ParseToMs_Valid(string input, int expectedMs)
    {
        Assert.True(Timecode.TryParseToMs(input, out var ms));
        Assert.Equal(expectedMs, ms);
        Assert.Equal(expectedMs, Timecode.ParseToMs(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("abc")]
    [InlineData("1:2:3:4")]
    [InlineData("1:60")]         // 60 seconds not allowed in M:S form
    [InlineData("1:60:00")]      // 60 minutes not allowed in H:M:S form
    [InlineData("1:00:60")]      // 60 seconds not allowed in H:M:S form
    [InlineData("--5")]
    public void ParseToMs_Invalid(string? input)
    {
        Assert.False(Timecode.TryParseToMs(input, out _));
        Assert.Throws<FormatException>(() => Timecode.ParseToMs(input!));
    }

    [Fact]
    public void Format_RoundTrips()
    {
        Assert.Equal("10:12.000", Timecode.Format(10 * 60_000 + 12_000));
        Assert.Equal("1:02:03.500", Timecode.Format(3_723_500));
        Assert.Equal("-00:05.000", Timecode.Format(-5_000));
    }
}
