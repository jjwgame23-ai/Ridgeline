using Godot;

namespace Ridgeline;

/// <summary>
/// Game time in seconds. Everything that schedules or compares times uses this
/// rather than the wall clock, so fixed-fps headless test runs behave like real play.
/// </summary>
public partial class Clock : Node
{
    public static double Now { get; private set; }
    /// <summary>The same, advanced per physics tick: for things that happen on ticks, several of which can fall in one frame.</summary>
    public static double PhysicsNow { get; private set; }

    public Clock()
    {
        ProcessPriority = -1000;
        ProcessPhysicsPriority = -1000;
    }

    public override void _EnterTree() => Now = PhysicsNow = 0;
    public override void _Process(double delta)
    {
        Now += delta;
        Conditions.Tick(Now);
    }
    public override void _PhysicsProcess(double delta) => PhysicsNow += delta;
}
