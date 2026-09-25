using System.Diagnostics;

namespace Ridgeline;

/// <summary>Cheap named timers for finding where frame time goes (printed by DevShot).</summary>
public static class Prof
{
    public static readonly Dictionary<string, (double Ms, long Calls)> Totals = new();

    public readonly struct Scope : IDisposable
    {
        readonly string _name;
        readonly long _start;
        public Scope(string name) { _name = name; _start = Stopwatch.GetTimestamp(); }
        public void Dispose()
        {
            double ms = (Stopwatch.GetTimestamp() - _start) * 1000.0 / Stopwatch.Frequency;
            var t = Totals.GetValueOrDefault(_name);
            Totals[_name] = (t.Ms + ms, t.Calls + 1);
        }
    }

    public static Scope Time(string name) => new(name);
}