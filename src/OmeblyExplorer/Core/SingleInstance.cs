// One Omebly Explorer: when it is already running and a folder is opened from somewhere else (the desktop, Win+E,
// another app), the running copy opens it in a new tab instead of a second copy starting. They talk over a named
// pipe that only this Windows user can open (no network, no ports), and the only thing that can be sent is a
// folder or file path to show, which is checked to exist.
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace OmeblyExplorer.Core;

public static class SingleInstance
{
    private static string PipeName => "OmeblyExplorer-" + (WindowsIdentity.GetCurrent().User?.Value ?? "user");

    /// <summary>True if a running copy took the request (this copy should then close).</summary>
    public static bool HandOver(string? path)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(400);
            using var w = new StreamWriter(client) { AutoFlush = true };
            w.WriteLine(path ?? "");
            return true;
        }
        catch { return false; }
    }

    /// <summary>Listens for other starts; <paramref name="open"/> gets a checked path, or "" for "just a new window".</summary>
    public static void Listen(Action<string> open)
    {
        var t = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    // CurrentUserOnly: Windows lets only this user open it (and checks the other end is this user).
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
                    server.WaitForConnection();
                    using var r = new StreamReader(server);
                    var line = (r.ReadLine() ?? "").Trim().Trim('"');
                    if (line.Length > 1024) continue;
                    if (line.Length == 0 || Directory.Exists(line) || File.Exists(line)) open(line);
                }
                catch (IOException) { Thread.Sleep(500); } // another copy owns the pipe name for a moment
                catch { Thread.Sleep(1000); }
            }
        }) { IsBackground = true, Name = "single instance" };
        t.Start();
    }
}
