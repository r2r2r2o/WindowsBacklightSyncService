using System.Runtime.InteropServices;

namespace WindowsBacklightSyncService.Services;

/// <summary>
/// Listens for system power events (sleep / resume / hibernate) without any UI.
/// A dedicated background thread owns a hidden top-level window with a message loop;
/// Windows broadcasts WM_POWERBROADCAST to top-level windows, so this works even in
/// session 0 (services) with no visible window ever created.
/// </summary>
public sealed class PowerEventMonitor : IDisposable
{
    private const uint WmPowerBroadcast = 0x0218;
    private const int PbtApmsuspend = 0x0004;
    private const int PbtApmresumesuspend = 0x0007;
    private const int PbtApmresumeautomatic = 0x0012;

    private readonly ILogger<PowerEventMonitor> _logger;
    private readonly WndProcDelegate _wndProc; // kept alive for the native window procedure
    private readonly string _className;        // unique per instance (see ctor)
    private readonly object _hwndGate = new();
    private Thread? _thread;
    private IntPtr _hwnd;
    private bool _classRegistered;
    private volatile bool _running;
    private long _lastResumeTick;

    /// <summary>True while the message-loop thread is intended to run (internal for tests).</summary>
    internal bool IsRunning => _running;

    /// <summary>Set once the hidden window exists and the message loop is about to pump (internal for tests).</summary>
    internal ManualResetEventSlim MessageLoopReady { get; } = new();

    /// <summary>Handle of the hidden message window (internal for tests to send messages).</summary>
    internal IntPtr WindowHandle
    {
        get
        {
            // _hwnd is written by the message-loop thread; a plain read could see a stale
            // value (non-volatile) — read it under the same lock the loop uses on write.
            lock (_hwndGate)
            {
                return _hwnd;
            }
        }
    }

    /// <summary>Raised when the system resumes from sleep/hibernate.</summary>
    public event Action? Resumed;

    public PowerEventMonitor(ILogger<PowerEventMonitor> logger)
    {
        _logger = logger;
        _wndProc = WndProc;
        // A UNIQUE class name per instance: RegisterClass registers a class once per
        // process, and every window created with that name dispatches through the FIRST
        // registration's wndproc. Sharing a name across instances would route messages of
        // later monitors to an earlier (possibly disposed) monitor — a real bug.
        _className = "BacklightSyncPowerEventWindow_" + Guid.NewGuid().ToString("N");
    }

    public void Start()
    {
        if (_running)
            return;
        _running = true;
        MessageLoopReady.Reset();
        _thread = new Thread(MessageLoop) { IsBackground = true, Name = "PowerEventMonitor" };
        _thread.Start();
    }

    private void MessageLoop()
    {
        try
        {
            var wndClass = new WndClass
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                lpszClassName = _className,
                hInstance = Native.GetModuleHandle(null),
            };

            if (Native.RegisterClass(ref wndClass) == 0
                && Marshal.GetLastWin32Error() != 1410 /* ERROR_CLASS_ALREADY_EXISTS */)
            {
                _logger.LogWarning("RegisterClass failed ({Error}) — power event monitoring unavailable.", Marshal.GetLastWin32Error());
                return;
            }
            _classRegistered = true;

            IntPtr hwnd = Native.CreateWindowEx(
                0, _className, _className,
                0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
            if (hwnd == IntPtr.Zero)
            {
                _logger.LogWarning("CreateWindowEx failed ({Error}) — power event monitoring unavailable.", Marshal.GetLastWin32Error());
                return;
            }
            lock (_hwndGate)
            {
                _hwnd = hwnd; // publish under the gate so WindowHandle readers see it
            }

            _logger.LogDebug("Power event window created (hwnd 0x{Handle:X}).", hwnd.ToInt64());

            // Signal readiness AFTER the window exists and right before pumping, so tests
            // (and any other caller) can post messages without racing the message loop.
            MessageLoopReady.Set();

            while (_running && Native.GetMessage(out Msg msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }

            Native.DestroyWindow(hwnd);
            lock (_hwndGate)
            {
                _hwnd = IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Power event monitor loop ended unexpectedly.");
        }
        finally
        {
            // Reset the running flag on EVERY exit path — otherwise a failed start or a
            // crashed loop would permanently block Start() (the monitor could never be
            // restarted, silently killing sleep/wake handling until the service restarts).
            _running = false;
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmPowerBroadcast)
        {
            switch ((int)wParam)
            {
                case PbtApmsuspend:
                    _logger.LogDebug("System is going to sleep.");
                    break;
                case PbtApmresumesuspend:
                case PbtApmresumeautomatic:
                    OnResume();
                    break;
            }
            return (IntPtr)1; // message handled
        }
        return Native.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void OnResume()
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastResumeTick) < 2000)
            return; // Windows fires both resume messages for a single wake-up
        Interlocked.Exchange(ref _lastResumeTick, now);

        _logger.LogInformation("System resumed from sleep/hibernate.");

        // Never block the message pump; dispatch handlers off-thread.
        try
        {
            Task.Run(() =>
            {
                try { Resumed?.Invoke(); }
                catch (Exception ex) { _logger.LogDebug(ex, "Resume handler failed."); }
            });
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to dispatch resume event.");
        }
    }

    public void Dispose()
    {
        _running = false;
        IntPtr hwnd;
        lock (_hwndGate)
        {
            hwnd = _hwnd;
        }
        if (hwnd != IntPtr.Zero)
            Native.PostMessage(hwnd, 0x0012 /* WM_QUIT */, IntPtr.Zero, IntPtr.Zero); // unblock GetMessage
        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;

        // Unregister our unique class so no stale wndproc survives (hygiene + test isolation).
        if (_classRegistered)
        {
            try
            {
                Native.UnregisterClass(_className, Native.GetModuleHandle(null));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "UnregisterClass failed.");
            }
            _classRegistered = false;
        }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    private static class Native
    {
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern ushort RegisterClass(ref WndClass lpWndClass);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern IntPtr CreateWindowEx(
            int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        internal static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        internal static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll")]
        internal static extern bool TranslateMessage(ref Msg lpMsg);

        [DllImport("user32.dll")]
        internal static extern IntPtr DispatchMessage(ref Msg lpMsg);

        [DllImport("user32.dll")]
        internal static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr GetModuleHandle(string? lpModuleName);
    }
}
