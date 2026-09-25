using Godot;

namespace Ridgeline;

/// <summary>Whatever the map's ground is — lets craters and props sit on it and blend in.</summary>
public interface IGround
{
    float HeightAt(float x, float z);
    Vector3 NormalAt(float x, float z);
    Color ColorAt(float x, float z);
}
