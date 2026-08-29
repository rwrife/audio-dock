using System.Globalization;

namespace AudioDock.Core.Models;

public readonly record struct VolumeLevel
{
    public VolumeLevel(double value)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Volume must be finite and between 0.0 and 1.0 inclusive.");
        }

        Value = value;
    }

    public double Value { get; }

    public override string ToString() => Value.ToString("0.###", CultureInfo.InvariantCulture);
}
