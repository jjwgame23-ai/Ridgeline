namespace Ridgeline;

/// <summary>
/// Made-up names with a Mediterranean sound, each used once per island. Hills are called by their height on the map
/// ("Hill 302"), as armies name them.
/// </summary>
public sealed class PlaceNames
{
    readonly Random _rng;
    readonly HashSet<string> _used = new();

    static readonly string[] Start = { "", "", "b", "c", "d", "f", "g", "l", "m", "n", "p", "r", "s", "t", "v", "z", "br", "cr", "gr", "pr", "tr", "st", "sc", "fr", "gl", "ch" };
    static readonly string[] Mid = { "l", "r", "n", "m", "s", "t", "d", "v", "c", "g", "z", "ll", "rr", "nn", "ss", "tt", "nt", "rt", "st", "lv", "rg", "sc", "nd", "rc", "lt" };
    static readonly string[] Vowel = { "a", "e", "i", "o", "u", "a", "o", "ia", "io", "ea", "au" };
    static readonly string[] End = { "a", "o", "e", "i", "ano", "ana", "ella", "ello", "ina", "ona", "osa", "eri", "aro", "eto", "olo", "ussa", "ena", "ica", "ola", "ate", "ori", "is", "os", "as", "ia", "ea", "ara" };

    public PlaceNames(int seed) => _rng = new Random(seed);

    string Pick(string[] a) => a[_rng.Next(a.Length)];

    string Word(int middles)
    {
        string w = Pick(Start) + Pick(Vowel);
        for (int k = 0; k < middles; k++) w += Pick(Mid) + Pick(Vowel);
        w += Pick(Mid) + Pick(End);
        return char.ToUpperInvariant(w[0]) + w[1..];
    }

    string Unique(Func<string> make)
    {
        for (int tries = 0; ; tries++)
        {
            string s = make();
            if (_used.Add(s) || tries > 40) return s;
        }
    }

    string Plain() => Word(_rng.NextDouble() < 0.6 ? 0 : 1);

    public string Island() => Unique(() => Word(1));

    public string Town(Town t) => Unique(() =>
    {
        double r = _rng.NextDouble();
        if (t.Port && r < 0.25) return "Porto " + Plain();
        if (t.Hilltop && r < 0.3) return r < 0.15 ? "Castel" + Plain().ToLowerInvariant() : "Rocca " + Plain();
        return Plain();
    });

    public string River() => Unique(Plain);

    public string Mountain() => Unique(() => new[] { "Monte ", "Pizzo ", "Punta " }[_rng.Next(3)] + Plain());
}
