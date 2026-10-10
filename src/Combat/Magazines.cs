namespace Ridgeline;

/// <summary>
/// The spare magazines someone carries, each with however many rounds are left in it. A reload takes the
/// fullest; the one coming off the gun goes back in a pouch if there's anything left in it (a retention reload,
/// a little slower), unless it's dropped for speed. An empty one is always dropped. (Magazines used to be
/// all-or-nothing, Squad-style: a partly used one was thrown away at every reload, and every round left in it.)
/// </summary>
public sealed class Magazines
{
    readonly List<int> _m = new();

    /// <summary>Rounds in a full one.</summary>
    public int Size { get; }

    public Magazines(int size, int count)
    {
        Size = Math.Max(1, size);
        Refill(count);
    }

    public int Count => _m.Count;
    public bool Any => _m.Count > 0;

    public int Full
    {
        get { int n = 0; foreach (var r in _m) if (r >= Size) n++; return n; }
    }

    public int Partial => _m.Count - Full;

    public int Rounds
    {
        get { int n = 0; foreach (var r in _m) n += r; return n; }
    }

    /// <summary>The most rounds in any one of them.</summary>
    public int Best
    {
        get { int b = 0; foreach (var r in _m) if (r > b) b = r; return b; }
    }

    /// <summary>Carrying this many rounds in all: full magazines, and what's over in one more.</summary>
    public void Hold(int rounds)
    {
        _m.Clear();
        while (rounds > 0)
        {
            _m.Add(Math.Min(Size, rounds));
            rounds -= Size;
        }
    }

    /// <summary>Someone else's magazines, as they are.</summary>
    public void CopyFrom(Magazines o)
    {
        _m.Clear();
        _m.AddRange(o._m);
    }

    /// <summary>A fresh load, all full (a resupply: part-used ones are swapped for full ones).</summary>
    public void Refill(int count)
    {
        _m.Clear();
        for (int i = 0; i < count; i++) _m.Add(Size);
    }

    /// <summary>
    /// Change magazines: out of the pouch comes the fullest, and its rounds are returned. The
    /// <paramref name="outgoing"/> rounds in the one coming off the gun go back in the pouch if
    /// <paramref name="keep"/> and there are any; otherwise it's dropped.
    /// </summary>
    public int Swap(int outgoing, bool keep)
    {
        if (_m.Count == 0) return 0;
        int bi = 0;
        for (int i = 1; i < _m.Count; i++)
            if (_m[i] > _m[bi]) bi = i;
        int got = _m[bi];
        _m.RemoveAt(bi);
        if (keep && outgoing > 0) _m.Add(outgoing);
        return got;
    }

    /// <summary>"5 spare mags, 2 part-used".</summary>
    public string Describe()
    {
        int p = Partial;
        return $"{Count} spare mag{(Count == 1 ? "" : "s")}{(p > 0 ? $", {p} part-used" : "")}";
    }

    /// <summary>Retention costs this much longer than dropping the magazine (putting it away in a pouch).</summary>
    public const float RetainTime = 0.6f;

    /// <summary>Rounds in the magazine on the gun, given what's in the weapon (one of them may be chambered).</summary>
    public static int InMag(int ammo, WeaponDef d) => d.MagSize == 1 ? 0 : Math.Max(0, ammo - d.Chambered);

    /// <summary>Is there a magazine worth changing to (fuller than the one on the gun)? A single-shot weapon only when it's empty.</summary>
    public bool Worth(int ammo, WeaponDef d) => Any && (d.MagSize == 1 ? ammo == 0 : Best > InMag(ammo, d));
}
