using AraOverlay.Core;

namespace AraOverlay.Core.Tests;

/// <summary>Covers parsing and rendering lap times.</summary>
public class TimeFormatTests
{
    [Theory]
    [InlineData("1:23.456", 83.456)]
    [InlineData("0:53.500", 53.5)]
    [InlineData("53.5", 53.5)]
    [InlineData("83.456", 83.456)]
    [InlineData("1:03.4", 63.4)]
    [InlineData("2:00.000", 120.0)]
    [InlineData(" 1:23.456 ", 83.456)]
    public void Parse_AcceptsBothForms(string text, double expected)
    {
        Assert.Equal(expected, TimeFormat.Parse(text), 3);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("1:2:3")]
    [InlineData("-5")]
    [InlineData("1:-3.0")]
    [InlineData("1:75.000")]
    [InlineData(":45.0")]
    public void Parse_RejectsGarbage(string text)
    {
        Assert.Throws<FormatException>(() => { TimeFormat.Parse(text); });
    }

    [Theory]
    [InlineData(83.456, "1:23.456")]
    [InlineData(53.5, "0:53.500")]
    [InlineData(0.0, "0:00.000")]
    [InlineData(3723.0, "62:03.000")]
    public void Format_RendersMinutesAndMillis(double seconds, string expected)
    {
        Assert.Equal(expected, TimeFormat.Format(seconds));
    }

    [Fact]
    public void Format_CarriesRoundingIntoTheMinute()
    {
        // 119.9996 must not render as "1:60.000"
        Assert.Equal("2:00.000", TimeFormat.Format(119.9996));
    }

    [Theory]
    [InlineData("1:23.456")]
    [InlineData("0:59.999")]
    [InlineData("12:00.001")]
    public void ParseAndFormat_RoundTrip(string text)
    {
        Assert.Equal(text, TimeFormat.Format(TimeFormat.Parse(text)));
    }
}
