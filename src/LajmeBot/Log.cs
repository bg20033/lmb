namespace LajmeBot;

public sealed class Log
{
    private readonly bool _verbose;
    public Log(bool verbose) => _verbose = verbose;

    /// <summary>Every warning and error of the run, shown in the GitHub summary.</summary>
    public List<string> Issues { get; } = new();

    private static string Now => DateTime.UtcNow.ToString("HH:mm:ss");
    public void Info(string m) => Console.WriteLine($"[{Now} INF] {m}");
    public void Warn(string m) { lock (Issues) Issues.Add(m); Console.WriteLine($"[{Now} WRN] {m}"); }
    public void Error(string m) { lock (Issues) Issues.Add("ERROR: " + m); Console.Error.WriteLine($"[{Now} ERR] {m}"); }
    public void Debug(string m) { if (_verbose) Console.WriteLine($"[{Now} DBG] {m}"); }
}
