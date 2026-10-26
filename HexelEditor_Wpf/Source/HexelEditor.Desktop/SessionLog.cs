using System.Globalization;
using System.IO;

namespace HexelEditor.Desktop;

internal sealed class SessionLog : IDisposable
{
    private readonly StreamWriter writer;
    private readonly object writeLock = new();

    public string FolderPath { get; }

    public SessionLog(string? verificationFolder)
    {
        var logRoot = Path.Combine(FindApplicationFolder(), "LogOutput");
        while (true)
        {
            FolderPath = verificationFolder ?? Path.Combine(logRoot, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(FolderPath);
            try
            {
                writer = new StreamWriter(new FileStream(Path.Combine(FolderPath, "Session.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { AutoFlush = true };
                break;
            }
            catch (IOException) when (verificationFolder == null && File.Exists(Path.Combine(FolderPath, "Session.log")))
            {
                // Each session owns a timestamp folder, including launches within the same second.
                Thread.Sleep(50);
            }
        }
        Write($"Application started. Process identifier: {Environment.ProcessId}. Version: {typeof(App).Assembly.GetName().Version}.");
    }

    private static string FindApplicationFolder()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "HexelEditor.slnx"))) return folder.FullName;
        }
        return AppContext.BaseDirectory;
    }

    public void Write(string message)
    {
        lock (writeLock) writer.WriteLine($"{DateTimeOffset.Now:O} {message}");
    }

    public void Dispose()
    {
        lock (writeLock) writer.Dispose();
    }
}
