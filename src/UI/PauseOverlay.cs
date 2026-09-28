using Godot;

namespace Ridgeline;

/// <summary>
/// Pause (P): the whole game stops, its clock with it, until you press it again. F10 still goes back to the
/// main menu from here. (There was no pause: stepping away left you standing in the fight.)
/// </summary>
public partial class PauseOverlay : CanvasLayer
{
    Label _label = null!;

    public override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        Layer = 100;
        _label = new Label
        {
            Text = "PAUSED\n\nP to resume · F10 main menu",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visible = false,
        };
        _label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _label.AddThemeFontSizeOverride("font_size", 28);
        _label.AddThemeColorOverride("font_outline_color", Colors.Black);
        _label.AddThemeConstantOverride("outline_size", 6);
        AddChild(_label);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e.IsActionPressed("pause"))
        {
            bool p = !GetTree().Paused;
            GetTree().Paused = p;
            _label.Visible = p;
            if (p) Input.MouseMode = Input.MouseModeEnum.Visible;
            GetViewport().SetInputAsHandled();
        }
        else if (GetTree().Paused && e.IsActionPressed("main_menu"))
        {
            GetTree().Paused = false;
            GetViewport().SetInputAsHandled();
            Main.Launch(this, null);
        }
    }
}
