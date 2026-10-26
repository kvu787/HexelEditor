using Godot;

namespace GodotAgentDevExperiment;

/// <summary>One log file per run, in the run's own timestamped folder under the application's LogOutput.
/// Every line also goes to Godot's own output, which the launcher captures with --log-file, so a run that
/// dies before flushing still leaves its last words somewhere.</summary>
public sealed class SessionLog : IDisposable
{
    private readonly StreamWriter _writer;

    public string Folder { get; }

    public SessionLog(string? folder)
    {
        if (string.IsNullOrEmpty(folder))
        {
            // res:// is the Godot project folder, Application, whose parent is the application's root.
            string applicationRoot = Path.GetFullPath(Path.Combine(ProjectSettings.GlobalizePath("res://"), ".."));
            folder = Path.Combine(applicationRoot, "LogOutput", DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
        }
        Directory.CreateDirectory(folder);
        Folder = folder;
        _writer = new StreamWriter(Path.Combine(folder, "Session.log"), append: true) { AutoFlush = true };
        Write("Application started.");
    }

    public void Write(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        _writer.WriteLine(line);
        GD.Print(line);
    }

    public void Dispose()
    {
        Write("Application exited.");
        _writer.Dispose();
    }
}
