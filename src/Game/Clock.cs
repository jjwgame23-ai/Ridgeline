using Godot;

namespace Ridgeline;

/// <summary>
/// Game time in seconds. Everything that schedules or compares times uses this
/// rather than the wall clock, so fixed-fps headless test runs behave like real play.
/// </summary>
public partial class Clock : Node
{
    public static double Now { get; private set; }

    public Clock() => ProcessPriority = -1000;

    public override void _EnterTree() => Now = 0;
    public override void _Process(double delta) => Now += delta;
}
