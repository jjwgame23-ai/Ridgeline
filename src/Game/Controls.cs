using Godot;

namespace Ridgeline;

public static class Controls
{
    public static void Register()
    {
        if (InputMap.HasAction("drone_fpv")) return; // already set up (scene reloads keep the InputMap)
        AddKey("move_forward", Key.W);
        AddKey("move_back", Key.S);
        AddKey("move_left", Key.A);
        AddKey("move_right", Key.D);
        AddKey("jump", Key.Space);
        AddKey("sprint", Key.Shift);
        AddKey("crouch", Key.C, Key.Ctrl);
        AddKey("prone", Key.Z);
        AddKey("lean_left", Key.Q);
        AddKey("lean_right", Key.E);
        AddKey("reload", Key.R);
        AddKey("firemode", Key.V);
        AddKey("check_ammo", Key.T);
        AddKey("grenade", Key.G);
        AddKey("weapon1", Key.Key1);
        AddKey("weapon2", Key.Key2);
        AddKey("gadget", Key.H);
        AddKey("drone_fpv", Key.J);
        AddKey("selfaid", Key.X);
        AddKey("use", Key.F);
        AddMouse("fire", MouseButton.Left);
        AddMouse("aim", MouseButton.Right);
    }

    static void Ensure(string action)
    {
        if (!InputMap.HasAction(action)) InputMap.AddAction(action);
    }

    static void AddKey(string action, params Key[] keys)
    {
        Ensure(action);
        foreach (var k in keys) InputMap.ActionAddEvent(action, new InputEventKey { PhysicalKeycode = k });
    }

    static void AddMouse(string action, MouseButton button)
    {
        Ensure(action);
        InputMap.ActionAddEvent(action, new InputEventMouseButton { ButtonIndex = button });
    }
}
