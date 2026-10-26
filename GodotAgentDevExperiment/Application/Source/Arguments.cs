using Godot;

namespace GodotAgentDevExperiment;

/// <summary>The application's own arguments, which come after Godot's "--" separator.</summary>
public sealed class Arguments
{
    public bool Checks { get; private set; }
    // The timing measurement alone, with none of the interface checks before it.
    public bool Measure { get; private set; }
    public string? LogFolder { get; private set; }
    public int Frames { get; private set; } = 240;
    // Seconds to stay open after the checks, so that a launcher can photograph the window from outside.
    public double HoldSeconds { get; private set; }
    public int Columns { get; private set; } = 48;
    public int Rows { get; private set; } = 40;
    public string? Piece { get; private set; }

    public static Arguments Parse(string[] arguments)
    {
        Arguments parsed = new();
        for (int i = 0; i < arguments.Length; i++)
        {
            string next = i + 1 < arguments.Length ? arguments[i + 1] : "";
            switch (arguments[i])
            {
                case "--checks": parsed.Checks = true; break;
                case "--measure": parsed.Checks = true; parsed.Measure = true; break;
                case "--log-folder": parsed.LogFolder = next; i++; break;
                case "--frames": parsed.Frames = int.Parse(next); i++; break;
                case "--hold": parsed.HoldSeconds = double.Parse(next, System.Globalization.CultureInfo.InvariantCulture); i++; break;
                case "--columns": parsed.Columns = int.Parse(next); i++; break;
                case "--rows": parsed.Rows = int.Parse(next); i++; break;
                case "--piece": parsed.Piece = next; i++; break;
                default: GD.PushWarning($"Unknown argument '{arguments[i]}' ignored."); break;
            }
        }
        return parsed;
    }
}
