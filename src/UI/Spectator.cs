using Godot;

namespace Ridgeline;

/// <summary>
/// Watching bots. Chase is over the shoulder; Eyes is exactly what the bot sees
/// down its own sights (the honest way to judge its aim); Free flies anywhere.
/// LMB / RMB switch bot, C switches Chase/Eyes, F toggles free camera.
/// </summary>
public partial class Spectator : Node3D
{
    public enum View { Chase, Eyes, Free }

    public IMatch Mode = null!;
    public View ViewMode = View.Chase;
    public Bot? Target { get; private set; }
    public bool Active { get; private set; }

    Camera3D _cam = null!;
    float _yaw, _pitch;
    double _targetDeadSince = -1;
    Bot? _hidden;

    public override void _Ready()
    {
        _cam = new Camera3D { Fov = 75f, Near = 0.05f, Far = 3000f };
        AddChild(_cam);
    }

    public void Activate(Bot? target)
    {
        Active = true;
        Target = target;
        _targetDeadSince = -1;
        _cam.MakeCurrent();
        if (target != null) GlobalPosition = target.EyePos - target.Aim.Dir * 3f;
    }

    /// <summary>Free camera, above and south of a point, looking down at it (a click on the map).</summary>
    public void FlyTo(Vector3 p)
    {
        ViewMode = View.Free;
        GlobalPosition = p + new Vector3(0f, 55f, 45f);
        _pitch = -48f;
        _yaw = 0f;
        RotationDegrees = new Vector3(_pitch, _yaw, 0f);
    }

    public void Deactivate()
    {
        if (_hidden != null && IsInstanceValid(_hidden)) _hidden.SetBodyVisible(true);
        _hidden = null;
        Active = false;
        _cam.Current = false;
    }

    void Cycle(int step)
    {
        var alive = Mode.Bots.Where(b => b.Alive).OrderBy(b => b.Team).ThenBy(b => b.Callsign).ToList();
        if (alive.Count == 0) return;
        int i = Target != null ? alive.IndexOf(Target) : -1;
        Target = alive[((i + step) % alive.Count + alive.Count) % alive.Count];
        _targetDeadSince = -1;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Active) return;
        // (The view toggle used to be C, which is also "down" in the free camera: going down left it.)
        bool captured = Input.MouseMode == Input.MouseModeEnum.Captured;
        if (captured && e.IsActionPressed("spectate_next")) Cycle(1);
        else if (captured && e.IsActionPressed("spectate_prev")) Cycle(-1);
        else if (e.IsActionPressed("spectate_view")) ViewMode = ViewMode == View.Chase ? View.Eyes : View.Chase;
        else if (e.IsActionPressed("spectate_free"))
        {
            if (ViewMode == View.Free) ViewMode = View.Chase;
            else
            {
                ViewMode = View.Free;
                var r = GlobalRotationDegrees;
                _pitch = r.X;
                _yaw = r.Y;
            }
        }
        else if (e is InputEventMouseMotion mm && ViewMode == View.Free && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            _yaw -= mm.Relative.X * 0.12f;
            _pitch = Mathf.Clamp(_pitch - mm.Relative.Y * 0.12f, -89f, 89f);
        }
    }

    public override void _Process(double delta)
    {
        if (!Active) return;
        float dt = (float)delta;

        if (ViewMode == View.Free)
        {
            var input = Input.GetVector("move_left", "move_right", "move_forward", "move_back");
            float up = (Input.IsActionPressed("jump") ? 1f : 0f) - (Input.IsActionPressed("crouch") ? 1f : 0f);
            float speed = Input.IsActionPressed("sprint") ? 40f : 10f;
            RotationDegrees = new Vector3(_pitch, _yaw, 0f);
            GlobalPosition += (GlobalBasis * new Vector3(input.X, 0f, input.Y) + Vector3.Up * up) * speed * dt;
            return;
        }

        if (Target == null || !IsInstanceValid(Target)) { Cycle(1); if (Target == null) return; }
        if (!Target.Alive)
        {
            if (_targetDeadSince < 0) _targetDeadSince = Clock.Now;
            else if (Clock.Now - _targetDeadSince > 2.5) Cycle(1);
        }

        var dir = Target.Aim.Dir;
        if (_hidden != null && IsInstanceValid(_hidden)) _hidden.SetBodyVisible(true);
        _hidden = null;
        if (ViewMode == View.Eyes && Target.Alive)
        {
            Target.SetBodyVisible(false); // we're inside its head
            _hidden = Target;
            GlobalPosition = Target.EyePos + dir * 0.1f;
            LookAt(GlobalPosition + dir, Vector3.Up);
            _cam.Fov = Mathf.Lerp(_cam.Fov, Target.Brain.WantsAds ? Target.Def.AdsFov + 8f : 75f, 1f - MathF.Exp(-dt * 10f));
            return;
        }

        _cam.Fov = 75f;
        var flat = new Vector3(dir.X, 0f, dir.Z).Normalized();
        var right = flat.Cross(Vector3.Up);
        var eye = Target.EyePos;
        var want = eye - flat * 3.0f + right * 0.6f + Vector3.Up * 0.55f;
        var hit = GetWorld3D().DirectSpaceState.IntersectRay(PhysicsRayQueryParameters3D.Create(eye, want, 1));
        if (hit.Count > 0) want = hit["position"].AsVector3() + (eye - want).Normalized() * 0.25f;
        GlobalPosition = GlobalPosition.Lerp(want, 1f - MathF.Exp(-dt * 10f));
        var look = eye + dir * 20f;
        if (look.DistanceTo(GlobalPosition) > 0.1f) LookAt(look, Vector3.Up);
    }
}
