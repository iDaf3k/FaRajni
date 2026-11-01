using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace FaRajni.App.Services;

/// <summary>One FaRajni per user: a second launch (e.g. double-clicking a video in Explorer while
/// the app is open) hands its file to the running instance over a named pipe and exits.</summary>
internal static class SingleInstance
{
    private static readonly string Name = $"FaRajni.{Environment.UserName}";
    private static Mutex? _mutex;

    /// <returns>true if this is the first instance.</returns>
    public static bool Claim()
    {
        _mutex = new Mutex(true, @"Local\" + Name, out var created);
        return created;
    }

    /// <summary>Send <paramref name="file"/> (or just "come to front") to the first instance.</summary>
    public static bool Forward(string? file)
    {
        try
        {
            AllowSetForegroundWindow(-1);   // let the running instance take the foreground
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.Out);
            pipe.Connect(3000);
            using var w = new StreamWriter(pipe);
            w.WriteLine(file ?? "");
            return true;
        }
        catch { return false; }
    }

    /// <summary>Calls <paramref name="onOpen"/> (on a background thread) for every forwarded launch.</summary>
    public static void Listen(Action<string?> onOpen)
    {
        new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(Name, PipeDirection.In, 1);
                    pipe.WaitForConnection();
                    using var r = new StreamReader(pipe);
                    var line = r.ReadLine();
                    onOpen(string.IsNullOrWhiteSpace(line) ? null : line);
                }
                catch (Exception ex) { AppPaths.LogError("SingleInstance", ex); Thread.Sleep(500); }
            }
        }) { IsBackground = true, Name = "SingleInstance" }.Start();
    }

    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
}
