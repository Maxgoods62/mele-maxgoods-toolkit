using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using LegendaryExplorerCore.Packages;

namespace MeleMcp.Core;

/// <summary>
/// Live link to a running LE game through ME3Tweaks' LEX Interop ASI, speaking the same protocol as Legendary Explorer:
/// commands go in over the named pipe <c>LEX_{game}_COMM_PIPE</c> (one connection per command), replies come back as
/// WM_COPYDATA to the first top-level window titled "Legendary Explorer". We own a hidden window that carries that title only
/// while one of our calls waits for a reply (<see cref="HoldReplies"/>), so several servers can share the link. Replies reach
/// us only while LEX itself is closed.
/// </summary>
public static class GameBridge
{
    public const string ReplyWindowTitle = "Legendary Explorer";

    /// <summary>Our window's title while no call of this server waits for a reply: one the LEX Interop ASIs do not look for.</summary>
    private const string IdleWindowTitle = "MeleMcp LEX replies (idle)";

    /// <summary>How many times this thread holds the reply window: the title goes on at the first and off at the last.</summary>
    [ThreadStatic] private static int _holds;

    /// <summary>
    /// Makes this server the one the games answer, until the result is disposed: takes the machine-wide lock, then gives our
    /// hidden window the title the LEX Interop ASIs look for. Disposing puts the idle title back, then releases the lock, on
    /// every way out. Dispose on the thread that called this (the lock is a mutex).
    /// </summary>
    internal static IDisposable HoldReplies()
    {
        var held = GameLock.ForLexReplies();
        try
        {
            if (_holds == 0) ReplyReceiver.Instance.SetTitle(ReplyWindowTitle);
            _holds++;
        }
        catch
        {
            held.Dispose();
            throw;
        }
        return new ReplyHold(held);
    }

    private sealed class ReplyHold(GameLock held) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                // Not checked: a window that is gone has no title left to take replies with.
                if (--_holds == 0) ReplyReceiver.Instance.TrySetTitle(IdleWindowTitle);
            }
            finally
            {
                held.Dispose();
            }
        }
    }

    private static readonly Dictionary<MEGame, (string Process, ulong Signature)> Targets = new()
    {
        [MEGame.LE1] = ("MassEffect1", 0x02AC00C7),
        [MEGame.LE2] = ("MassEffect2", 0x02AC00C6),
        [MEGame.LE3] = ("MassEffect3", 0x02AC00C5),
    };

    /// <summary>
    /// LEX Interop runs queued commands from its ProcessEvent hook, before the original call. A console command goes through
    /// ProcessEvent itself, so commands queued together run nested, the last one first. A gap lets each one run alone.
    /// </summary>
    private const int MinGapMs = 50;
    private static readonly object SendGate = new();
    private static long _lastSend;

    public static string PipeName(MEGame game) => $"LEX_{game}_COMM_PIPE";

    /// <summary>The game's process name, e.g. MassEffect1.</summary>
    public static string ProcessName(MEGame game) => Target(game).Process;

    public static bool IsGameRunning(MEGame game)
    {
        var processes = Process.GetProcessesByName(Target(game).Process);
        foreach (var p in processes) p.Dispose();
        return processes.Length > 0;
    }

    /// <summary>The running game's process, by id and start time (ids are reused), or null. The start is 0 when Windows does not say.</summary>
    internal static (int Id, long Started)? RunningProcess(MEGame game)
    {
        var processes = Process.GetProcessesByName(Target(game).Process);
        try
        {
            if (processes.MinBy(p => p.Id) is not { } process) return null;
            try
            {
                return (process.Id, process.StartTime.ToFileTimeUtc());
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return (process.Id, 0);
            }
        }
        finally
        {
            foreach (var p in processes) p.Dispose();
        }
    }

    /// <summary>Enumerating \\.\pipe\ does not connect, so it cannot steal the ASI's single pipe instance.</summary>
    public static bool IsPipeOpen(MEGame game) =>
        Directory.EnumerateFiles(@"\\.\pipe\").Any(p => p.EndsWith(@"\" + PipeName(game), StringComparison.OrdinalIgnoreCase));

    public static void Send(MEGame game, string command)
    {
        if (command.Length >= 1024) throw new ArgumentException("Interop commands must be shorter than 1024 characters.");
        Paced(() =>
        {
            using var pipe = Connect(game);
            // Same framing as LEX: the trailing \r keeps old interop ASIs from reading past the end.
            pipe.Write(Encoding.ASCII.GetBytes(command + "\0\r"));
            pipe.Flush();
        });
    }

    /// <summary>Hands a package to the game from memory; the game's package lookup then finds it instead of the file on disk.</summary>
    public static void SendPackage(MEGame game, string packageName, byte[] data)
    {
        Paced(() =>
        {
            using var pipe = Connect(game);
            pipe.Write(Encoding.ASCII.GetBytes($"PRECACHE_FILE {packageName} {data.Length}\0"));
            pipe.Flush();
            pipe.Write(data);
            pipe.Flush();
        });
    }

    private static void Paced(Action send)
    {
        lock (SendGate)
        {
            var wait = _lastSend + MinGapMs - Environment.TickCount64;
            if (wait > 0) Thread.Sleep((int)wait);
            send();
            _lastSend = Environment.TickCount64;
        }
    }

    /// <summary>
    /// Sends a command and waits for the first reply that matches, ignoring replies that arrived before it. Replies carry no
    /// request id, so a late reply to an earlier request that timed out still matches: GameLink fences after a timeout.
    /// </summary>
    public static string SendAndWait(MEGame game, string command, Func<string, bool> isReply, TimeSpan timeout)
    {
        var receiver = ReplyReceiver.Instance;
        var mark = receiver.Mark();
        Send(game, command);
        return receiver.WaitFor(Target(game).Signature, mark, isReply, timeout)
            ?? throw new TimeoutException($"No reply to '{Truncate(command)}' within {timeout.TotalSeconds:0.#} s. "
                                          + "Is LEX closed (it takes the replies) and the game window not paused in a menu?");
    }

    /// <summary>Most recent reply that matches, from anything received while this server held the reply window, without sending.</summary>
    public static string? LastReply(MEGame game, Func<string, bool> isReply) => ReplyReceiver.Instance.Last(Target(game).Signature, isReply);

    /// <summary>
    /// Top-level windows titled "Legendary Explorer" while this server holds the title: 1 is ours alone. More means LEX is open,
    /// or a mele-mcp server from before the title was shared is still running; either may take the replies. Takes the reply
    /// window itself, for the count.
    /// </summary>
    public static int ReplyWindowCount()
    {
        using var replies = HoldReplies();
        var count = 0;
        var hwnd = IntPtr.Zero;
        while ((hwnd = Native.FindWindowExW(IntPtr.Zero, hwnd, null, ReplyWindowTitle)) != IntPtr.Zero) count++;
        return count;
    }

    private static NamedPipeClientStream Connect(MEGame game)
    {
        if (!IsGameRunning(game)) throw new InvalidOperationException($"{game} is not running.");
        if (!IsPipeOpen(game))
        {
            throw new InvalidOperationException($"Pipe {PipeName(game)} not found: the LEX Interop ASI is not installed, or the game has not finished starting.");
        }
        var pipe = new NamedPipeClientStream(".", PipeName(game), PipeDirection.Out);
        pipe.Connect(3000);
        return pipe;
    }

    private static (string Process, ulong Signature) Target(MEGame game) =>
        Targets.TryGetValue(game, out var t) ? t : throw new ArgumentException($"No live link for {game}.");

    private static string Truncate(string s) => s.Length <= 80 ? s : s[..80] + "...";

    /// <summary>Hidden window on its own thread with a message loop; stores every WM_COPYDATA string it gets.</summary>
    private sealed class ReplyReceiver
    {
        private static readonly object InstanceGate = new();
        private static ReplyReceiver? _instance;
        private static int _attempts;

        /// <summary>
        /// Made on first use. A window that could not be made is tried again at the next use: a Lazy would keep that first
        /// exception and refuse every LE1 LEX Interop tool until the server restarts. One attempt at a time, so this server never
        /// has a second window. Each starts with the idle title, and only <see cref="HoldReplies"/> gives it "Legendary Explorer".
        /// </summary>
        public static ReplyReceiver Instance
        {
            get
            {
                lock (InstanceGate) return _instance ??= new ReplyReceiver(++_attempts);
            }
        }

        private const int MaxKept = 200;
        private readonly List<(long Seq, ulong Signature, string Text)> _messages = [];
        private long _seq;
        private readonly Native.WndProc _wndProc; // kept alive: the window calls it from native code
        private IntPtr _hwnd; // set on the window's thread before the constructor returns

        private ReplyReceiver(int attempt)
        {
            _wndProc = WndProc;
            // A class that a failed attempt registered points at that attempt's window procedure, which is gone: a retry registers its own.
            var className = attempt == 1 ? "MeleMcpLexReplyWindow" : $"MeleMcpLexReplyWindow{attempt}";
            var ready = new ManualResetEventSlim();
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try { CreateWindow(className); }
                catch (Exception ex) { failure = ex; }
                ready.Set();
                if (failure is null) Native.RunMessageLoop();
            }) { IsBackground = true, Name = "LEX reply window" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
            if (failure is not null) throw failure;
        }

        public long Mark() { lock (_messages) return _seq; }

        public string? WaitFor(ulong signature, long after, Func<string, bool> isReply, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            lock (_messages)
            {
                while (true)
                {
                    foreach (var m in _messages)
                    {
                        if (m.Seq > after && m.Signature == signature && isReply(m.Text)) return m.Text;
                    }
                    var left = deadline - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || !Monitor.Wait(_messages, left)) return null;
                }
            }
        }

        public string? Last(ulong signature, Func<string, bool> isReply)
        {
            lock (_messages) return _messages.LastOrDefault(m => m.Signature == signature && isReply(m.Text)).Text;
        }

        public void SetTitle(string title)
        {
            if (!TrySetTitle(title)) throw new InvalidOperationException($"SetWindowText failed: {Marshal.GetLastWin32Error()}");
        }

        /// <summary>From any thread: Windows hands the change to the window's own thread, whose message loop is always running.</summary>
        public bool TrySetTitle(string title) => Native.SetWindowTextW(_hwnd, title);

        private void CreateWindow(string className)
        {
            var wc = new Native.WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<Native.WNDCLASSEXW>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = Native.GetModuleHandleW(null),
                lpszClassName = className,
            };
            if (Native.RegisterClassExW(ref wc) == 0) throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
            // A normal (not message-only) top-level window, never shown: FindWindow in the game must be able to see it while it
            // carries the title (HoldReplies). It starts idle, so the game answers another server's window until we hold it.
            var hwnd = Native.CreateWindowExW(0, className, IdleWindowTitle, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
            _hwnd = hwnd;
        }

        private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            const uint WM_COPYDATA = 0x004A;
            if (msg != WM_COPYDATA) return Native.DefWindowProcW(hwnd, msg, wParam, lParam);

            var cds = Marshal.PtrToStructure<Native.COPYDATASTRUCT>(lParam);
            var text = cds.lpData == IntPtr.Zero ? "" : Marshal.PtrToStringUni(cds.lpData, (int)cds.cbData / 2).TrimEnd('\0');
            lock (_messages)
            {
                _messages.Add((++_seq, (ulong)cds.dwData, text));
                if (_messages.Count > MaxKept) _messages.RemoveAt(0);
                Monitor.PulseAll(_messages);
            }
            return 1;
        }
    }

    private static class Native
    {
        public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASSEXW
        {
            public uint cbSize, style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra;
            public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            public string? lpszMenuName, lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public uint cbData;
            public IntPtr lpData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam, lParam;
            public uint time;
            public int ptX, ptY;
        }

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr CreateWindowExW(uint exStyle, string className, string title, uint style, int x, int y, int w, int h,
            IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? title);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowTextW(IntPtr hwnd, string text);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandleW(string? name);

        [DllImport("user32.dll")]
        private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage(ref MSG msg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessageW(ref MSG msg);

        public static void RunMessageLoop()
        {
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
        }
    }
}
