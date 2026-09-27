namespace LajmeBot;

public sealed class Log
{
    private readonly bool _verbose;
    public Log(bool verbose) => _verbose = verbose;

    private static string Now => DateTime.UtcNow.ToString("HH:mm:ss");
    public void Info(string m) => Console.WriteLine($"[{Now} INF] {m}");
    public void Warn(string m) => Console.WriteLine($"[{Now} WRN] {m}");
    public void Error(string m) => Console.Error.WriteLine($"[{Now} ERR] {m}");
    public void Debug(string m) { if (_verbose) Console.WriteLine($"[{Now} DBG] {m}"); }
}
