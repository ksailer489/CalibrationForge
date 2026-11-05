using System.Globalization;

namespace CalibrationForge.Core;

public sealed class TableAxis
{
    public TableAxis(string name, string unit, IEnumerable<decimal> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        ArgumentNullException.ThrowIfNull(values);
        Name = name;
        Unit = unit;
        Values = Array.AsReadOnly(values.ToArray());
    }

    public string Name { get; }
    public string Unit { get; }
    public IReadOnlyList<decimal> Values { get; }
    public string Caption => $"{Name} ({Unit})";
    public string Label(int index) => $"{Values[index].ToString(CultureInfo.InvariantCulture)} {Unit}";
}
