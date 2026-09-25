using Godot;

namespace Ridgeline;

/// <summary>
/// Who a bot is. Skill drives the mechanical numbers; aggression, patience and
/// courage drive decisions. The same merc keeps the same personality across rounds.
/// </summary>
public sealed class Personality
{
    public string Name = "";
    public float Skill;          // 0..1 overall
    public float ReactionS;      // seconds from noticing a target to starting the flick
    public float FlickTau;       // how fast the error from a flick settles (s)
    public float TremorDeg;      // aim wobble while aiming down sights
    public float RecoilControl;  // 0..1: share of recoil pulled back down
    public float TurnSpeed;      // deg/s, the fastest they can swing
    public float SpotRate;       // how quickly they notice things
    public float Tracking;       // 0..1: how well they follow and lead a moving target
    public float Aggression;     // 0 careful .. 1 pushes and trades
    public float Patience;       // seconds they'll hold an angle before acting
    public float Courage;        // resistance to suppression
    public bool Marksman;

    public static readonly string[] Callsigns =
    {
        "Rook", "Viper", "Tusk", "Ghost", "Mako", "Brick", "Lynx", "Hatch", "Dozer", "Saint",
        "Kodiak", "Wren", "Fitz", "Havoc", "Jester", "Nomad", "Pike", "Rasp", "Sable", "Vance",
        "Bishop", "Cobra", "Dutch", "Flint", "Gus", "Hex", "Jinx", "Knox", "Loki", "Moss",
        "Nash", "Oakes", "Quill", "Rhino", "Shep", "Tank", "Ursa", "Wolfe", "Yates", "Zulu",
        "Ash", "Badger", "Cash", "Diesel", "Echo", "Fox", "Grit", "Hawk", "Irons", "Jax",
        "Kilo", "Lance", "Mule", "Ned", "Ozzy", "Pox", "Rex", "Slate", "Tex", "Vex",
        "Axle", "Boone", "Crow", "Drift", "Ember", "Frost", "Gage", "Hollis", "Ike", "Judd",
        "Kane", "Lark", "Mace", "Nix", "Orca", "Pip", "Reno", "Scout", "Tate", "Vic",
        "Arlo", "Bram", "Cutter", "Dane", "Edge", "Finch", "Gunner", "Holt", "Iggy", "Jett",
        "Kip", "Lowe", "Moxie", "Noel", "Otis", "Price", "Rudy", "Stroud", "Tully", "Wade",
    };

    public static Personality Roll(RandomNumberGenerator rng, string name)
    {
        float s = Mathf.Clamp(rng.Randfn(0.58f, 0.17f), 0.15f, 0.97f);
        float J() => rng.RandfRange(0.85f, 1.2f); // individual quirks around the skill curve
        return new Personality
        {
            Name = name,
            Skill = s,
            ReactionS = Mathf.Lerp(0.36f, 0.15f, s) * J(),
            FlickTau = Mathf.Lerp(0.45f, 0.13f, s) * J(),
            TremorDeg = Mathf.Lerp(0.95f, 0.28f, s) * J(),
            RecoilControl = Mathf.Clamp(Mathf.Lerp(0.3f, 0.85f, s) + rng.RandfRange(-0.1f, 0.1f), 0f, 1f),
            TurnSpeed = Mathf.Lerp(230f, 560f, s) * J(),
            SpotRate = Mathf.Lerp(1.1f, 2.1f, s) * J(),
            Tracking = Mathf.Clamp(Mathf.Lerp(0.4f, 0.9f, s) + rng.RandfRange(-0.1f, 0.1f), 0f, 1f),
            Aggression = rng.RandfRange(0.3f, 1f),
            Patience = rng.RandfRange(1.5f, 5f),
            Courage = rng.RandfRange(0.2f, 1f),
            Marksman = rng.Randf() < 0.2f,
        };
    }
}
