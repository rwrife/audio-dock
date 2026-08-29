using AudioDock.Core.Models;

namespace AudioDock.Core.Tests.Models;

public sealed class VolumeLevelTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(0.5)]
    [InlineData(1)]
    public void AcceptsInclusiveSafeBounds(double value)
    {
        Assert.Equal(value, new VolumeLevel(value).Value);
    }

    [Theory]
    [InlineData(-0.001)]
    [InlineData(1.001)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void RejectsUnsafeOrNonFiniteValues(double value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VolumeLevel(value));
    }
}
