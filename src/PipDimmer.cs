// PipDimmer - Chrome 子母畫面滾輪透明控制器
//
// Hover a Chromium picture-in-picture window and scroll to change its opacity.
// Ctrl+scroll does the same for any other window. Ctrl+middle-click makes a window
// click-through ("ghost", like Spooky View).
//
// Build with the in-box .NET Framework compiler (see build.ps1). That compiler is the
// pre-Roslyn C# 5.0 one, so: no string interpolation, no ?., no nameof, no expression-bodied
// members, no auto-property initializers, no `out var`.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

// System.Threading (for Mutex) and System.Windows.Forms both define Timer. Every timer here
// must be the WinForms one so its Tick fires on the UI thread alongside the message loop.
using Timer = System.Windows.Forms.Timer;

namespace PipDimmer
{
    #region ---------- Diagnostic log ----------

    // Off unless the exe is started with -log. Never written from the hook callback: the hook
    // only bumps counters, and the UI thread flushes them.
    internal static class Log
    {
        private static string _path;
        public static bool On;

        public static void Enable()
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PipDimmer");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "log.txt");
                File.WriteAllText(_path, "", new UTF8Encoding(false));
                On = true;
            }
            catch { On = false; }
        }

        public static string PathOf() { return _path; }

        public static void W(string line)
        {
            if (!On || _path == null) return;
            try
            {
                File.AppendAllText(_path,
                    DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + line +
                    Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
        }
    }

    #endregion

    #region ---------- Win32 ----------

    internal static class N
    {
        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;

        public const int WS_MINIMIZE = 0x20000000;
        public const int WS_MAXIMIZEBOX = 0x00010000;
        public const int WS_MINIMIZEBOX = 0x00020000;

        public const int WS_EX_TOPMOST = 0x00000008;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public const uint LWA_ALPHA = 0x00000002;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_FRAMECHANGED = 0x0020;
        public const uint SWP_STYLE = SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED;

        public const uint RDW_INVALIDATE = 0x0001;
        public const uint RDW_ERASE = 0x0004;
        public const uint RDW_ALLCHILDREN = 0x0080;
        public const uint RDW_UPDATENOW = 0x0100;
        public const uint RDW_FRAME = 0x0400;
        public const uint RDW_FULL = RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW;

        public const int WH_MOUSE_LL = 14;
        public const int WM_MOUSEWHEEL = 0x020A;
        public const int WM_MBUTTONDOWN = 0x0207;
        public const int WM_MBUTTONUP = 0x0208;
        public const uint LLMHF_INJECTED = 0x00000001;

        public const int GA_ROOT = 2;

        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12;      // Alt
        public const int VK_LWIN = 0x5B;
        public const int VK_RWIN = 0x5C;

        // Modifier bitmask stored in settings and used by the hook.
        public const int MOD_CTRL = 1;
        public const int MOD_SHIFT = 2;
        public const int MOD_ALT = 4;
        public const int MOD_WIN = 8;

        public const uint KEYEVENTF_KEYUP = 0x0002;

        public const uint EVENT_OBJECT_SHOW = 0x8002;
        public const uint EVENT_OBJECT_HIDE = 0x8003;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        public const int OBJID_WINDOW = 0;

        public const int SW_SHOWNOACTIVATE = 4;

        public const int WM_CLOSE = 0x0010;
        public const int WM_APP = 0x8000;
        public const int WM_APP_WHEEL = WM_APP + 1;
        public const int WM_APP_GHOST = WM_APP + 2;

        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        public const int ERROR_ACCESS_DENIED = 5;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        public delegate void WinEventProc(IntPtr hHook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")]
        public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint evMin, uint evMax, IntPtr hmod, WinEventProc cb, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandleW(string name);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr h, int flags);
        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")]
        public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr h, int cmd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(IntPtr h, StringBuilder buf, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr h, StringBuilder buf, int max);
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtrW(IntPtr h, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtrW(IntPtr h, int index, IntPtr value);

        public static int GetStyle(IntPtr h) { return unchecked((int)(long)GetWindowLongPtrW(h, GWL_STYLE)); }
        public static int GetExStyle(IntPtr h) { return unchecked((int)(long)GetWindowLongPtrW(h, GWL_EXSTYLE)); }
        public static void SetExStyle(IntPtr h, int value)
        {
            SetWindowLongPtrW(h, GWL_EXSTYLE, new IntPtr((long)(uint)value));
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        public static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr rgn, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool PostMessageW(IntPtr h, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("shcore.dll")]
        public static extern int SetProcessDpiAwareness(int value);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, StringBuilder buf, ref uint size);
        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);
    }

    #endregion

    #region ---------- Window helpers ----------

    internal static class W
    {
        [ThreadStatic] private static StringBuilder _buf;

        private static StringBuilder Buf()
        {
            if (_buf == null) _buf = new StringBuilder(512);
            _buf.Length = 0;
            return _buf;
        }

        public static string ClassOf(IntPtr h)
        {
            StringBuilder b = Buf();
            int n = N.GetClassNameW(h, b, b.Capacity);
            return n > 0 ? b.ToString(0, n) : string.Empty;
        }

        public static string TextOf(IntPtr h)
        {
            StringBuilder b = Buf();
            int n = N.GetWindowTextW(h, b, b.Capacity);
            return n > 0 ? b.ToString(0, n) : string.Empty;
        }

        // Chromium's PiP window class is the same as an ordinary browser window
        // (Chrome_WidgetWin_1) -- what separates them is WS_EX_TOPMOST. Measured on
        // Chrome/Win11: PiP ex=0x00200108, ordinary window ex=0x00200100.
        public const string ChromiumClass = "Chrome_WidgetWin_1";

        private static readonly string[] BrowserProcs = new string[]
        {
            "chrome", "msedge", "brave", "vivaldi", "opera", "opera_gx",
            "chromium", "thorium", "arc", "yandex", "whale"
        };

        // Localized titles of the classic video PiP window. Chromium sets these from
        // IDS_PICTURE_IN_PICTURE_TITLE_TEXT, so the string follows the BROWSER's UI language,
        // not the OS language. Matched case-insensitively; note the Windows English string is
        // "Picture in picture" with a lowercase second word.
        // Only a fallback: the structural test below is the primary path, and Document PiP
        // (YouTube miniplayer, Meet) carries the page title here instead, so it never matches.
        private static readonly string[] PipTitles = new string[]
        {
            "子母畫面",                        // zh-TW
            "画中画",                          // zh-CN
            "畫中畫",
            "Picture in picture",              // en (Windows)
            "Picture in Picture",              // en (mac wording, harmless to accept)
            "Picture-in-Picture",
            "ピクチャー イン ピクチャー",       // ja
            "PIP 모드",                        // ko
            "Bild im Bild",                    // de
            "Imagen en imagen",                // es
            "Mode PIP (Picture-in-Picture)"    // fr
        };

        // A pinned-always-on-top ordinary browser window would otherwise look exactly like a PiP
        // window to a topmost-only test, and we would silently eat the wheel on it. Real PiP
        // windows carry neither box: measured style 0x16CC0000 (PiP) vs 0x36CF0000 (ordinary).
        // Settable to false via settings.ini if a future Chrome build ships a PiP window that
        // does have both boxes.
        public static bool RequirePipShape = true;

        private static readonly Dictionary<uint, string> _procCache = new Dictionary<uint, string>();
        private static int _procCacheStamp = Environment.TickCount;

        public static string ProcessNameOf(uint pid)
        {
            // PIDs get recycled, so age the cache out rather than trusting it forever.
            if (Environment.TickCount - _procCacheStamp > 60000)
            {
                _procCache.Clear();
                _procCacheStamp = Environment.TickCount;
            }
            string cached;
            if (_procCache.TryGetValue(pid, out cached)) return cached;

            string name = string.Empty;
            IntPtr h = N.OpenProcess(N.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    StringBuilder sb = new StringBuilder(512);
                    uint size = (uint)sb.Capacity;
                    if (N.QueryFullProcessImageNameW(h, 0, sb, ref size))
                        name = Path.GetFileNameWithoutExtension(sb.ToString(0, (int)size)).ToLowerInvariant();
                }
                catch { }
                finally { N.CloseHandle(h); }
            }
            _procCache[pid] = name;
            return name;
        }

        public static bool IsBrowserProcess(string procName)
        {
            if (string.IsNullOrEmpty(procName)) return false;
            for (int i = 0; i < BrowserProcs.Length; i++)
                if (string.Equals(BrowserProcs[i], procName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static bool LooksLikePipTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return false;
            for (int i = 0; i < PipTitles.Length; i++)
                if (string.Equals(PipTitles[i], title, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // True when the window has BOTH caption boxes, i.e. it is shaped like an ordinary
        // browser window rather than a floating PiP overlay.
        public static bool HasBothCaptionBoxes(int style)
        {
            return (style & N.WS_MINIMIZEBOX) != 0 && (style & N.WS_MAXIMIZEBOX) != 0;
        }

        // Runs on the UI thread only: ProcessNameOf needs OpenProcess and is not cheap enough
        // for a low-level hook callback. Results are cached into Hook.PipSet for O(1) lookups.
        // Cheap rejections come first so OpenProcess only runs for genuine candidates.
        public static bool IsPipWindow(IntPtr h, uint ownPid)
        {
            if (h == IntPtr.Zero || !N.IsWindow(h) || !N.IsWindowVisible(h)) return false;

            int style = N.GetStyle(h);
            if ((style & N.WS_MINIMIZE) != 0) return false;
            if (ClassOf(h) != ChromiumClass) return false;

            int exStyle = N.GetExStyle(h);

            // Chromium spawns transient topmost overlays (tooltips, autofill dropdowns, bubbles)
            // that are also Chrome_WidgetWin_1 and also topmost. They are non-interactive, marked
            // WS_EX_NOACTIVATE; a real PiP window can be clicked and activated, so it never is.
            // Observed false positive without this: ex=0x08280028.
            if ((exStyle & N.WS_EX_NOACTIVATE) != 0) return false;

            // Chrome will not let a PiP window get smaller than roughly 260x146.
            N.RECT rc;
            if (!N.GetWindowRect(h, out rc)) return false;
            if ((rc.Right - rc.Left) < 120 || (rc.Bottom - rc.Top) < 80) return false;

            bool topmost = (exStyle & N.WS_EX_TOPMOST) != 0;
            bool pipShaped = !RequirePipShape || !HasBothCaptionBoxes(style);
            bool titled = LooksLikePipTitle(TextOf(h));

            // Structural path catches both video PiP and Document PiP regardless of language;
            // the title path is the safety net if a build ever stops marking PiP topmost.
            if (!((topmost && pipShaped) || titled)) return false;

            uint pid;
            N.GetWindowThreadProcessId(h, out pid);
            if (pid == ownPid) return false;
            return IsBrowserProcess(ProcessNameOf(pid));
        }

        public static bool IsShellWindow(IntPtr h)
        {
            string c = ClassOf(h);
            return c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd"
                || c == "Shell_SecondaryTrayWnd" || c == "NotifyIconOverflowWindow"
                || c == "Windows.UI.Core.CoreWindow" || c == "MultitaskingViewFrame";
        }

        public static byte CurrentAlphaOf(IntPtr h)
        {
            if ((N.GetExStyle(h) & N.WS_EX_LAYERED) != 0)
            {
                uint key; byte a; uint flags;
                if (N.GetLayeredWindowAttributes(h, out key, out a, out flags) && (flags & N.LWA_ALPHA) != 0)
                    return a;
            }
            return 255;
        }

        public static byte PctToAlpha(int pct)
        {
            int a = (int)Math.Round(pct * 255.0 / 100.0);
            if (a < 1) a = 1;
            if (a > 255) a = 255;
            return (byte)a;
        }

        public static int AlphaToPct(byte a)
        {
            return (int)Math.Round(a * 100.0 / 255.0);
        }

        public static string ModName(int mask)
        {
            if (mask == 0) return "無";
            string s = "";
            if ((mask & N.MOD_CTRL) != 0) s += "Ctrl+";
            if ((mask & N.MOD_SHIFT) != 0) s += "Shift+";
            if ((mask & N.MOD_ALT) != 0) s += "Alt+";
            if ((mask & N.MOD_WIN) != 0) s += "Win+";
            return s.Substring(0, s.Length - 1);
        }
    }

    #endregion

    #region ---------- Low level mouse hook ----------

    // The hook callback does nothing but decide "swallow or not" and PostMessage the work to the
    // UI thread. MSDN's guidance for LowLevelMouseProc is to hand work off and return immediately:
    // the callback may exceed LowLevelHooksTimeout (default 300ms) only 10 times -- on the 11th
    // Windows silently unhooks you, with no way for the app to find out. Cross-process calls like
    // SetLayeredWindowAttributes can block on Chrome's UI thread, so they must never run in here.
    internal static class Hook
    {
        // Whole immutable objects are swapped in by the UI thread; reference writes are atomic so
        // the callback can never observe a half-built collection.
        internal static volatile HashSet<IntPtr> PipSet = new HashSet<IntPtr>();
        internal static volatile IntPtr[] Ghosts = new IntPtr[0];

        internal static volatile bool Enabled = true;
        internal static volatile bool AllowOthers = true;
        internal static volatile bool GhostGesture = true;

        // Which modifiers must be held to act on a window that is NOT a PiP window.
        // Ctrl alone is deliberately not the default: Ctrl+wheel is zoom in browsers,
        // VS Code, Explorer and Office, and swallowing it would break all of them.
        internal static volatile int ModMask = N.MOD_CTRL | N.MOD_SHIFT;

        private static bool Down(int vk)
        {
            return (N.GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        internal static bool ModifiersHeld()
        {
            int m = ModMask;
            if (m == 0) return true;
            if ((m & N.MOD_CTRL) != 0 && !Down(N.VK_CONTROL)) return false;
            if ((m & N.MOD_SHIFT) != 0 && !Down(N.VK_SHIFT)) return false;
            if ((m & N.MOD_ALT) != 0 && !Down(N.VK_MENU)) return false;
            if ((m & N.MOD_WIN) != 0 && !Down(N.VK_LWIN) && !Down(N.VK_RWIN)) return false;
            return true;
        }

        internal static IntPtr MsgHwnd = IntPtr.Zero;
        internal static IntPtr OsdHwnd = IntPtr.Zero;

        // Watchdog: bumped on every callback entry, including plain mouse-moves.
        internal static int LastTick = Environment.TickCount;

        // Diagnostic counters. Plain int stores, cheap enough for the callback; the UI thread
        // reads and reports them so no I/O ever happens inside the hook.
        internal static int CbTotal, CbWheel, CbSwallowed;
        internal static IntPtr LastTarget;
        internal static int LastResultCode;   // 0=passed through, 1=swallowed

        // MSDN, SetWindowsHookEx: "In .NET apps, you must ensure the callback is not moved around
        // by the garbage collector (otherwise your app will crash with an ExecutionEngineException)."
        private static readonly N.HookProc _proc = new N.HookProc(Callback);
        private static GCHandle _pin;
        private static IntPtr _handle = IntPtr.Zero;
        private static bool _swallowNextMiddleUp;

        internal static bool IsInstalled { get { return _handle != IntPtr.Zero; } }

        internal static bool Install()
        {
            if (_handle != IntPtr.Zero) return true;
            if (!_pin.IsAllocated) _pin = GCHandle.Alloc(_proc);

            // Passing IntPtr.Zero for hMod with dwThreadId == 0 can fail with
            // ERROR_HOOK_NEEDS_HMOD (1428), so hand it a real module handle.
            IntPtr hMod = IntPtr.Zero;
            try
            {
                string mod = System.Diagnostics.Process.GetCurrentProcess().MainModule.ModuleName;
                hMod = N.GetModuleHandleW(mod);
            }
            catch { }

            _handle = N.SetWindowsHookEx(N.WH_MOUSE_LL, _proc, hMod, 0);
            int err = Marshal.GetLastWin32Error();
            LastTick = Environment.TickCount;
            Log.W(string.Format("SetWindowsHookEx -> 0x{0:X} hMod=0x{1:X} err={2}",
                _handle.ToInt64(), hMod.ToInt64(), err));
            return _handle != IntPtr.Zero;
        }

        internal static void Uninstall()
        {
            if (_handle != IntPtr.Zero)
            {
                N.UnhookWindowsHookEx(_handle);
                _handle = IntPtr.Zero;
            }
        }

        internal static bool Reinstall()
        {
            Uninstall();
            return Install();
        }

        private static IntPtr Next(int nCode, IntPtr w, IntPtr l)
        {
            return N.CallNextHookEx(IntPtr.Zero, nCode, w, l);
        }

        private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            LastTick = Environment.TickCount;
            CbTotal++;
            if (nCode < 0) return Next(nCode, wParam, lParam);

            int msg = (int)wParam;
            // The overwhelming majority of events are WM_MOUSEMOVE and leave on this line.
            if (msg != N.WM_MOUSEWHEEL && msg != N.WM_MBUTTONDOWN && msg != N.WM_MBUTTONUP)
                return Next(nCode, wParam, lParam);

            if (msg == N.WM_MBUTTONUP)
            {
                // Swallowing the down without its up leaves some apps in a stuck drag state.
                if (_swallowNextMiddleUp) { _swallowNextMiddleUp = false; return new IntPtr(1); }
                return Next(nCode, wParam, lParam);
            }

            if (!Enabled || MsgHwnd == IntPtr.Zero) return Next(nCode, wParam, lParam);

            // Deliberately NOT filtering LLMHF_INJECTED. Remote-desktop tools, some touchpad
            // drivers and VM guest additions all mark their input as injected, and dropping it
            // would make PipDimmer silently dead in those sessions. There is no feedback-loop
            // risk to guard against either: this app never synthesizes mouse input.
            N.MSLLHOOKSTRUCT data = (N.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(N.MSLLHOOKSTRUCT));

            if (msg == N.WM_MOUSEWHEEL) CbWheel++;

            IntPtr target = Resolve(data.pt);
            LastTarget = target;
            LastResultCode = 0;
            if (target == IntPtr.Zero) return Next(nCode, wParam, lParam);

            bool isPip = PipSet.Contains(target);
            bool mods = ModifiersHeld();

            if (msg == N.WM_MBUTTONDOWN)
            {
                if (GhostGesture && mods && (isPip || AllowOthers) && !W.IsShellWindow(target))
                {
                    N.PostMessageW(MsgHwnd, N.WM_APP_GHOST, IntPtr.Zero, target);
                    _swallowNextMiddleUp = true;
                    return new IntPtr(1);
                }
                return Next(nCode, wParam, lParam);
            }

            // WM_MOUSEWHEEL. PiP needs no modifier; anything else needs the configured
            // modifiers, so ordinary scrolling everywhere else is completely untouched.
            if (!isPip)
            {
                if (!AllowOthers || !mods) return Next(nCode, wParam, lParam);
                if (W.IsShellWindow(target)) return Next(nCode, wParam, lParam);
            }

            short delta = unchecked((short)((data.mouseData >> 16) & 0xFFFF));
            if (delta == 0) return Next(nCode, wParam, lParam);

            N.PostMessageW(MsgHwnd, N.WM_APP_WHEEL, new IntPtr(delta > 0 ? 1 : -1), target);
            CbSwallowed++;
            LastResultCode = 1;
            return new IntPtr(1);
        }

        private static IntPtr Resolve(N.POINT pt)
        {
            // WindowFromPoint skips WS_EX_TRANSPARENT windows, so a ghosted target would become
            // unreachable -- and unrecoverable -- if we did not check the ghost list by rect first.
            // Ghosts is stored in z-order (top first) by the UI thread's EnumWindows scan.
            IntPtr[] ghosts = Ghosts;
            for (int i = 0; i < ghosts.Length; i++)
            {
                N.RECT r;
                if (!N.GetWindowRect(ghosts[i], out r)) continue;
                if (pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom)
                    return ghosts[i];
            }

            IntPtr h = N.WindowFromPoint(pt);
            if (h == IntPtr.Zero) return IntPtr.Zero;
            h = N.GetAncestor(h, N.GA_ROOT);
            if (h == IntPtr.Zero || h == OsdHwnd) return IntPtr.Zero;
            return h;
        }
    }

    #endregion

    #region ---------- Settings ----------

    internal sealed class Settings
    {
        public bool Enabled;
        public int StepPercent;
        public int MinPercent;
        public int LastAlphaPercent;
        public bool ShowOsd;
        public bool AutoApply;
        public bool AllowOtherWindows;
        public bool GhostGesture;
        public bool InvertWheel;
        public bool ForceRedraw;
        public bool RequirePipShape;   // ini-only escape hatch, see W.RequirePipShape
        public int ModifierMask;       // N.MOD_* bitmask; 0 = no modifier required

        private readonly string _path;

        public Settings()
        {
            Enabled = true;
            StepPercent = 5;
            MinPercent = 10;
            LastAlphaPercent = 60;
            ShowOsd = true;
            AutoApply = true;
            AllowOtherWindows = true;
            GhostGesture = true;
            InvertWheel = false;
            ForceRedraw = false;
            RequirePipShape = true;
            ModifierMask = N.MOD_CTRL | N.MOD_SHIFT;

            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PipDimmer");
            _path = Path.Combine(dir, "settings.ini");
        }

        public string Path_ { get { return _path; } }

        public void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;
                foreach (string raw in File.ReadAllLines(_path, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq).Trim();
                    string v = line.Substring(eq + 1).Trim();
                    Apply(k, v);
                }
            }
            catch { }
            Clamp();
        }

        private void Apply(string k, string v)
        {
            switch (k)
            {
                case "Enabled": Enabled = Bool(v, Enabled); break;
                case "StepPercent": StepPercent = Int(v, StepPercent); break;
                case "MinPercent": MinPercent = Int(v, MinPercent); break;
                case "LastAlphaPercent": LastAlphaPercent = Int(v, LastAlphaPercent); break;
                case "ShowOsd": ShowOsd = Bool(v, ShowOsd); break;
                case "AutoApply": AutoApply = Bool(v, AutoApply); break;
                case "AllowOtherWindows": AllowOtherWindows = Bool(v, AllowOtherWindows); break;
                case "GhostGesture": GhostGesture = Bool(v, GhostGesture); break;
                case "InvertWheel": InvertWheel = Bool(v, InvertWheel); break;
                case "ForceRedraw": ForceRedraw = Bool(v, ForceRedraw); break;
                case "RequirePipShape": RequirePipShape = Bool(v, RequirePipShape); break;
                case "ModifierMask": ModifierMask = Int(v, ModifierMask); break;
            }
        }

        private static bool Bool(string v, bool fallback)
        {
            if (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)) return true;
            if (v == "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase)) return false;
            return fallback;
        }
        private static int Int(string v, int fallback)
        {
            int r;
            if (int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r)) return r;
            return fallback;
        }

        public void Clamp()
        {
            if (StepPercent != 2 && StepPercent != 5 && StepPercent != 10) StepPercent = 5;
            if (MinPercent != 10 && MinPercent != 20 && MinPercent != 30) MinPercent = 10;
            if (LastAlphaPercent < MinPercent) LastAlphaPercent = MinPercent;
            if (LastAlphaPercent > 100) LastAlphaPercent = 100;
            if (ModifierMask < 0 || ModifierMask > 15) ModifierMask = N.MOD_CTRL | N.MOD_SHIFT;
        }

        public void Save()
        {
            try
            {
                Clamp();
                string dir = Path.GetDirectoryName(_path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# PipDimmer settings");
                sb.AppendLine("Enabled=" + (Enabled ? "1" : "0"));
                sb.AppendLine("StepPercent=" + StepPercent.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("MinPercent=" + MinPercent.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("LastAlphaPercent=" + LastAlphaPercent.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("ShowOsd=" + (ShowOsd ? "1" : "0"));
                sb.AppendLine("AutoApply=" + (AutoApply ? "1" : "0"));
                sb.AppendLine("AllowOtherWindows=" + (AllowOtherWindows ? "1" : "0"));
                sb.AppendLine("GhostGesture=" + (GhostGesture ? "1" : "0"));
                sb.AppendLine("InvertWheel=" + (InvertWheel ? "1" : "0"));
                sb.AppendLine("ForceRedraw=" + (ForceRedraw ? "1" : "0"));
                sb.AppendLine("# Modifier bitmask for acting on non-PiP windows:");
                sb.AppendLine("#   1=Ctrl  2=Shift  4=Alt  8=Win   (add them together, 0 = none)");
                sb.AppendLine("ModifierMask=" + ModifierMask.ToString(CultureInfo.InvariantCulture));
                sb.AppendLine("# Set to 0 only if a PiP window stops being detected because it");
                sb.AppendLine("# has both a minimize and a maximize box.");
                sb.AppendLine("RequirePipShape=" + (RequirePipShape ? "1" : "0"));
                File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }

    #endregion

    #region ---------- OSD ----------

    internal sealed class Osd : Form
    {
        private string _label = "";
        private int _pct = 100;
        private bool _bar = true;
        private readonly Timer _hide;
        private readonly Font _fontBig;
        private readonly Font _fontSmall;

        public Osd()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            BackColor = Color.FromArgb(24, 24, 27);
            Opacity = 0.94;          // makes WinForms add WS_EX_LAYERED -> clean overlay
            Size = new Size(200, 70);

            Font baseFont = SystemFonts.MessageBoxFont;   // matches the user's UI locale/font
            _fontBig = new Font(baseFont.FontFamily, 15f, FontStyle.Bold, GraphicsUnit.Point);
            _fontSmall = new Font(baseFont.FontFamily, 9f, FontStyle.Regular, GraphicsUnit.Point);

            _hide = new Timer();
            _hide.Interval = 850;
            _hide.Tick += delegate { _hide.Stop(); Visible = false; };
        }

        // Never steal focus from whatever the user is actually working in.
        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= N.WS_EX_TRANSPARENT | N.WS_EX_NOACTIVATE | N.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyRegion();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyRegion();
        }

        private void ApplyRegion()
        {
            using (GraphicsPath p = Rounded(new Rectangle(0, 0, Width, Height), 12))
                Region = new Region(p);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d - 1, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d - 1, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public void Flash(string label, int pct, bool showBar, Point near)
        {
            _label = label;
            _pct = pct;
            _bar = showBar;

            Screen sc = Screen.FromPoint(near);
            Rectangle wa = sc.WorkingArea;
            int x = near.X + 22;
            int y = near.Y + 24;
            if (x + Width > wa.Right) x = near.X - Width - 22;
            if (y + Height > wa.Bottom) y = near.Y - Height - 24;
            if (x < wa.Left) x = wa.Left;
            if (y < wa.Top) y = wa.Top;
            Location = new Point(x, y);

            if (!Visible) Visible = true;
            Invalidate();
            _hide.Stop();
            _hide.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Rectangle full = new Rectangle(0, 0, Width - 1, Height - 1);
            using (GraphicsPath p = Rounded(full, 12))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(28, 28, 32)))
            using (Pen edge = new Pen(Color.FromArgb(78, 78, 88)))
            {
                g.FillPath(bg, p);
                g.DrawPath(edge, p);
            }

            if (_bar)
            {
                TextRenderer.DrawText(g, _label, _fontSmall, new Point(14, 10),
                    Color.FromArgb(168, 168, 178), TextFormatFlags.NoPadding);
                string pctText = _pct.ToString(CultureInfo.InvariantCulture) + "%";
                TextRenderer.DrawText(g, pctText, _fontBig, new Point(12, 24),
                    Color.White, TextFormatFlags.NoPadding);

                Rectangle track = new Rectangle(14, Height - 16, Width - 28, 5);
                using (SolidBrush tb = new SolidBrush(Color.FromArgb(58, 58, 66)))
                using (GraphicsPath tp = Rounded(track, 2))
                    g.FillPath(tb, tp);

                int w = (int)Math.Round(track.Width * (_pct / 100.0));
                if (w > 4)
                {
                    Rectangle fill = new Rectangle(track.X, track.Y, w, track.Height);
                    using (SolidBrush fb = new SolidBrush(Color.FromArgb(96, 172, 255)))
                    using (GraphicsPath fp = Rounded(fill, 2))
                        g.FillPath(fb, fp);
                }
            }
            else
            {
                Rectangle box = new Rectangle(12, 0, Width - 24, Height);
                TextRenderer.DrawText(g, _label, _fontBig, box, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.WordBreak);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_hide != null) _hide.Dispose();
                if (_fontBig != null) _fontBig.Dispose();
                if (_fontSmall != null) _fontSmall.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    #endregion

    #region ---------- Message-only window ----------

    internal sealed class MsgWindow : NativeWindow
    {
        public const string WindowCaption = "PipDimmerMsgWindow";

        private readonly Action<int, IntPtr> _onWheel;
        private readonly Action<IntPtr> _onGhost;
        private readonly Action _onClose;

        public MsgWindow(Action<int, IntPtr> onWheel, Action<IntPtr> onGhost, Action onClose)
        {
            _onWheel = onWheel;
            _onGhost = onGhost;
            _onClose = onClose;
            CreateParams cp = new CreateParams();
            cp.Caption = WindowCaption;
            cp.Parent = new IntPtr(-3);   // HWND_MESSAGE
            CreateHandle(cp);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == N.WM_APP_WHEEL)
            {
                _onWheel((int)m.WParam, m.LParam);
                return;
            }
            if (m.Msg == N.WM_APP_GHOST)
            {
                _onGhost(m.LParam);
                return;
            }
            if (m.Msg == N.WM_CLOSE)
            {
                // Lets a script stop PipDimmer the same way the tray menu does, so every window
                // it touched gets restored instead of being left half-transparent by a kill.
                _onClose();
                return;
            }
            base.WndProc(ref m);
        }
    }

    #endregion

    #region ---------- Tray application ----------

    internal sealed class TrayApp : ApplicationContext
    {
        private readonly Settings _cfg = new Settings();
        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly Osd _osd = new Osd();
        private readonly MsgWindow _msg;

        private readonly Dictionary<IntPtr, byte> _managed = new Dictionary<IntPtr, byte>();
        private readonly HashSet<IntPtr> _ghosts = new HashSet<IntPtr>();
        private HashSet<IntPtr> _knownPips = new HashSet<IntPtr>();

        private readonly Timer _scan = new Timer();
        private readonly Timer _scanSoon = new Timer();
        private readonly Timer _watchdog = new Timer();
        private readonly Timer _saveSoon = new Timer();
        private readonly Timer _countdown = new Timer();

        private readonly N.EnumWindowsProc _enumProc;
        private readonly N.WinEventProc _winEventProc;
        private IntPtr _winEventHook = IntPtr.Zero;
        private GCHandle _winEventPin;

        private readonly uint _ownPid;
        private IntPtr _iconHandle = IntPtr.Zero;
        private bool _accessDeniedWarned;
        private bool _firstScan = true;
        private int _countdownLeft;
        private string _statusText = "";

        // scratch for EnumWindows, reused to avoid per-scan allocation churn
        private HashSet<IntPtr> _tmpPips;
        private List<IntPtr> _tmpGhosts;

        private ToolStripMenuItem _miEnabled, _miOsd, _miAuto, _miOthers, _miGhost,
                                  _miInvert, _miRedraw, _miAutostart, _miStatus;
        private ToolStripMenuItem _miStep, _miMin, _miMod;

        public TrayApp()
        {
            _ownPid = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            _cfg.Load();

            _enumProc = new N.EnumWindowsProc(EnumCallback);
            _winEventProc = new N.WinEventProc(OnWinEvent);
            _winEventPin = GCHandle.Alloc(_winEventProc);

            _msg = new MsgWindow(OnWheel, OnGhostToggle, ExitApp);
            Hook.MsgHwnd = _msg.Handle;

            // Touching Handle forces creation, so the hook can exclude the OSD by HWND
            // from its very first callback.
            Hook.OsdHwnd = _osd.Handle;

            BuildMenu();
            PushSettingsToHook();

            _tray.Icon = BuildIcon();
            _tray.ContextMenuStrip = _menu;
            _tray.Text = "PipDimmer";
            _tray.Visible = true;
            _tray.MouseUp += TrayMouseUp;

            _scan.Interval = 1000;
            _scan.Tick += delegate { Scan(); };
            _scan.Start();

            _scanSoon.Interval = 250;      // debounce after a browser window appears
            _scanSoon.Tick += delegate { _scanSoon.Stop(); Scan(); };

            _watchdog.Interval = 2000;
            _watchdog.Tick += WatchdogTick;
            _watchdog.Start();

            _saveSoon.Interval = 1500;
            _saveSoon.Tick += delegate { _saveSoon.Stop(); _cfg.Save(); };

            _countdown.Interval = 1000;
            _countdown.Tick += CountdownTick;

            if (!Hook.Install())
                _tray.ShowBalloonTip(5000, "PipDimmer",
                    "滑鼠鉤子掛載失敗，滾輪控制無法運作。可從系統匣選單「重新掛載滑鼠鉤子」再試一次。",
                    ToolTipIcon.Error);

            _winEventHook = N.SetWinEventHook(
                N.EVENT_OBJECT_SHOW, N.EVENT_OBJECT_HIDE, IntPtr.Zero, _winEventProc, 0, 0,
                N.WINEVENT_OUTOFCONTEXT | N.WINEVENT_SKIPOWNPROCESS);

            SystemEvents.SessionSwitch += OnSessionSwitch;
            SystemEvents.SessionEnding += OnSessionEnding;
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            Application.ApplicationExit += delegate { Cleanup(); };

            Scan();
            UpdateStatus();
        }

        #region menu

        private void BuildMenu()
        {
            // Keep the image margin: that is where ToolStripMenuItem.Checked draws its tick.
            ToolStripMenuItem title = new ToolStripMenuItem("PipDimmer — 子母畫面透明控制");
            title.Enabled = false;
            _menu.Items.Add(title);

            _miStatus = new ToolStripMenuItem("目前：尚未偵測到子母畫面");
            _miStatus.Enabled = false;
            _menu.Items.Add(_miStatus);

            _menu.Items.Add(new ToolStripSeparator());

            _miEnabled = Check("啟用", _cfg.Enabled, delegate
            {
                _cfg.Enabled = !_cfg.Enabled;
                _miEnabled.Checked = _cfg.Enabled;
                PushSettingsToHook();
                SaveSoon();
                UpdateStatus();
            });

            _miStep = new ToolStripMenuItem("每格步進");
            AddChoice(_miStep, "2%", 2, _cfg.StepPercent, delegate(int v)
            {
                _cfg.StepPercent = v; SyncChoice(_miStep, v); SaveSoon();
            });
            AddChoice(_miStep, "5%", 5, _cfg.StepPercent, delegate(int v)
            {
                _cfg.StepPercent = v; SyncChoice(_miStep, v); SaveSoon();
            });
            AddChoice(_miStep, "10%", 10, _cfg.StepPercent, delegate(int v)
            {
                _cfg.StepPercent = v; SyncChoice(_miStep, v); SaveSoon();
            });
            _menu.Items.Add(_miStep);

            _miMin = new ToolStripMenuItem("最低透明度");
            AddChoice(_miMin, "10%", 10, _cfg.MinPercent, delegate(int v)
            {
                _cfg.MinPercent = v; SyncChoice(_miMin, v); SaveSoon();
            });
            AddChoice(_miMin, "20%", 20, _cfg.MinPercent, delegate(int v)
            {
                _cfg.MinPercent = v; SyncChoice(_miMin, v); SaveSoon();
            });
            AddChoice(_miMin, "30%", 30, _cfg.MinPercent, delegate(int v)
            {
                _cfg.MinPercent = v; SyncChoice(_miMin, v); SaveSoon();
            });
            _menu.Items.Add(_miMin);

            _miOsd = Check("顯示 OSD 百分比", _cfg.ShowOsd, delegate
            {
                _cfg.ShowOsd = !_cfg.ShowOsd; _miOsd.Checked = _cfg.ShowOsd; SaveSoon();
            });

            _miAuto = Check("新的子母畫面自動套用上次透明度", _cfg.AutoApply, delegate
            {
                _cfg.AutoApply = !_cfg.AutoApply; _miAuto.Checked = _cfg.AutoApply; SaveSoon();
            });

            // Ctrl alone is offered but not the default: Ctrl+wheel is the zoom shortcut in
            // browsers, VS Code, Explorer and Office, and Ctrl+middle-click opens links in a
            // new tab. Win and Alt are usable but pop the Start menu / menu bar on release
            // unless masked, which is why Ctrl+Shift is the default.
            _miMod = new ToolStripMenuItem("修飾鍵（其他視窗）");
            AddModChoice(_miMod, "無（直接滾，會吃掉所有視窗的滾輪）", 0);
            AddModChoice(_miMod, "Shift", N.MOD_SHIFT);
            AddModChoice(_miMod, "Alt", N.MOD_ALT);
            AddModChoice(_miMod, "Win", N.MOD_WIN);
            AddModChoice(_miMod, "Ctrl（會蓋掉瀏覽器縮放）", N.MOD_CTRL);
            AddModChoice(_miMod, "Ctrl+Shift", N.MOD_CTRL | N.MOD_SHIFT);
            AddModChoice(_miMod, "Ctrl+Alt", N.MOD_CTRL | N.MOD_ALT);
            AddModChoice(_miMod, "Shift+Alt", N.MOD_SHIFT | N.MOD_ALT);
            _menu.Items.Add(_miMod);

            _miOthers = Check("", _cfg.AllowOtherWindows, delegate
            {
                _cfg.AllowOtherWindows = !_cfg.AllowOtherWindows;
                _miOthers.Checked = _cfg.AllowOtherWindows;
                PushSettingsToHook(); SaveSoon();
            });

            _miGhost = Check("", _cfg.GhostGesture, delegate
            {
                _cfg.GhostGesture = !_cfg.GhostGesture;
                _miGhost.Checked = _cfg.GhostGesture;
                PushSettingsToHook(); SaveSoon();
            });

            SyncModLabels();

            _miInvert = Check("反轉滾輪方向", _cfg.InvertWheel, delegate
            {
                _cfg.InvertWheel = !_cfg.InvertWheel; _miInvert.Checked = _cfg.InvertWheel; SaveSoon();
            });

            _miRedraw = Check("套用後強制重繪（畫面異常時打開）", _cfg.ForceRedraw, delegate
            {
                _cfg.ForceRedraw = !_cfg.ForceRedraw; _miRedraw.Checked = _cfg.ForceRedraw; SaveSoon();
            });

            _menu.Items.Add(new ToolStripSeparator());

            _menu.Items.Add(new ToolStripMenuItem("還原所有視窗", null, delegate
            {
                RestoreAll();
                FlashText("已還原所有視窗");
            }));

            _menu.Items.Add(new ToolStripMenuItem("偵測視窗資訊…", null, delegate
            {
                StartDiagnostics();
            }));

            _menu.Items.Add(new ToolStripMenuItem("重新掛載滑鼠鉤子", null, delegate
            {
                bool ok = Hook.Reinstall();
                FlashText(ok ? "滑鼠鉤子已重新掛載" : "重新掛載失敗");
            }));

            _miAutostart = Check("開機自動啟動", Autostart.IsEnabled(), delegate
            {
                bool want = !_miAutostart.Checked;
                string err = Autostart.Set(want);
                _miAutostart.Checked = Autostart.IsEnabled();
                if (err != null)
                    MessageBox.Show("設定開機自動啟動失敗：\n" + err, "PipDimmer",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                else if (want && Autostart.IsDisabledByTaskManager())
                    MessageBox.Show(
                        "捷徑已建立，但工作管理員的「開機」分頁把 PipDimmer 設為停用，所以開機時不會啟動。\n\n" +
                        "請到工作管理員 → 開機應用程式，把 PipDimmer 改成「已啟用」。",
                        "PipDimmer", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });

            _menu.Items.Add(new ToolStripSeparator());
            _menu.Items.Add(new ToolStripMenuItem("結束", null, delegate { ExitApp(); }));

            _menu.Opening += delegate
            {
                // Task Manager can disable a Startup-folder entry without deleting the shortcut,
                // so re-read the real state every time instead of trusting our own last write.
                _miAutostart.Checked = Autostart.IsEnabled();
            };
        }

        private ToolStripMenuItem Check(string text, bool state, EventHandler onClick)
        {
            ToolStripMenuItem mi = new ToolStripMenuItem(text, null, onClick);
            mi.CheckOnClick = false;
            mi.Checked = state;
            _menu.Items.Add(mi);
            return mi;
        }

        private static void AddChoice(ToolStripMenuItem parent, string text, int value, int current, Action<int> onPick)
        {
            ToolStripMenuItem mi = new ToolStripMenuItem(text);
            mi.Tag = value;
            mi.Checked = (value == current);
            mi.Click += delegate { onPick(value); };
            parent.DropDownItems.Add(mi);
        }

        private void AddModChoice(ToolStripMenuItem parent, string text, int mask)
        {
            ToolStripMenuItem mi = new ToolStripMenuItem(text);
            mi.Tag = mask;
            mi.Checked = (mask == _cfg.ModifierMask);
            mi.Click += delegate
            {
                _cfg.ModifierMask = mask;
                SyncChoice(_miMod, mask);
                SyncModLabels();
                PushSettingsToHook();
                SaveSoon();
                FlashText("修飾鍵：" + W.ModName(mask));
            };
            parent.DropDownItems.Add(mi);
        }

        // The two gesture labels name the current modifier, so the menu always tells the
        // truth about what to press.
        private void SyncModLabels()
        {
            string m = W.ModName(_cfg.ModifierMask);
            if (_miOthers != null)
                _miOthers.Text = (_cfg.ModifierMask == 0)
                    ? "允許滾輪調整其他視窗（不需修飾鍵）"
                    : "允許 " + m + "+滾輪 調整其他視窗";
            if (_miGhost != null)
                _miGhost.Text = (_cfg.ModifierMask == 0)
                    ? "中鍵 切換滑鼠穿透"
                    : m + "+中鍵 切換滑鼠穿透";
        }

        private static void SyncChoice(ToolStripMenuItem parent, int value)
        {
            foreach (ToolStripItem it in parent.DropDownItems)
            {
                ToolStripMenuItem mi = it as ToolStripMenuItem;
                if (mi != null && mi.Tag is int) mi.Checked = ((int)mi.Tag == value);
            }
        }

        private void TrayMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            _cfg.Enabled = !_cfg.Enabled;
            _miEnabled.Checked = _cfg.Enabled;
            PushSettingsToHook();
            SaveSoon();
            UpdateStatus();
            FlashText(_cfg.Enabled ? "PipDimmer 已啟用" : "PipDimmer 已停用");
        }

        #endregion

        #region core

        private void PushSettingsToHook()
        {
            Hook.Enabled = _cfg.Enabled;
            Hook.AllowOthers = _cfg.AllowOtherWindows;
            Hook.GhostGesture = _cfg.GhostGesture;
            Hook.ModMask = _cfg.ModifierMask;
            W.RequirePipShape = _cfg.RequirePipShape;
        }

        private void SaveSoon()
        {
            _saveSoon.Stop();
            _saveSoon.Start();
        }

        private void OnWheel(int dir, IntPtr h)
        {
            Log.W(string.Format("OnWheel dir={0} hwnd=0x{1:X} isWindow={2}",
                dir, h.ToInt64(), N.IsWindow(h)));
            if (h == IntPtr.Zero || !N.IsWindow(h)) return;

            byte cur;
            if (!_managed.TryGetValue(h, out cur)) cur = W.CurrentAlphaOf(h);

            if (_cfg.InvertWheel) dir = -dir;

            int step = (int)Math.Round(_cfg.StepPercent * 255.0 / 100.0);
            if (step < 1) step = 1;

            int min = W.PctToAlpha(_cfg.MinPercent);
            if (min < 26) min = 26;      // alpha 0 would make the window unclickable and unrecoverable

            int next = cur + dir * step;
            if (next > 255) next = 255;
            if (next < min) next = min;
            if (next == cur) { ShowAlphaOsd(h, (byte)cur); return; }

            ApplyAlpha(h, (byte)next);
            _cfg.LastAlphaPercent = W.AlphaToPct((byte)next);
            SaveSoon();
            ShowAlphaOsd(h, (byte)next);
            UpdateStatus();
            MaskWinKeyIfNeeded();
        }

        // Windows opens the Start menu when the Win key is released with no keystroke in
        // between, so a Win+wheel gesture would pop Start every single time. AutoHotkey solves
        // this the same way: inject a harmless Ctrl tap so the release reads as a combination.
        // Runs on the UI thread, never from the hook, and only while Win is actually held.
        private void MaskWinKeyIfNeeded()
        {
            if ((_cfg.ModifierMask & N.MOD_WIN) == 0) return;
            if ((N.GetAsyncKeyState(N.VK_LWIN) & 0x8000) == 0 &&
                (N.GetAsyncKeyState(N.VK_RWIN) & 0x8000) == 0) return;
            N.keybd_event((byte)N.VK_CONTROL, 0, 0, UIntPtr.Zero);
            N.keybd_event((byte)N.VK_CONTROL, 0, N.KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private void ApplyAlpha(IntPtr h, byte alpha)
        {
            int ex = N.GetExStyle(h);

            if (alpha >= 255)
            {
                if ((ex & N.WS_EX_LAYERED) != 0)
                {
                    // Set 255 BEFORE clearing the style bit -- AutoHotkey's docs note this avoids
                    // redraw artefacts, and SetWindowLongPtr changes stay cached until SetWindowPos.
                    N.SetLayeredWindowAttributes(h, 0, 255, N.LWA_ALPHA);
                    N.SetExStyle(h, ex & ~N.WS_EX_LAYERED);
                    N.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, N.SWP_STYLE);
                    N.RedrawWindow(h, IntPtr.Zero, IntPtr.Zero, N.RDW_FULL);
                }
                _managed.Remove(h);
                return;
            }

            bool addedLayered = false;
            if ((ex & N.WS_EX_LAYERED) == 0)
            {
                N.SetExStyle(h, ex | N.WS_EX_LAYERED);
                // Documented requirement: window data is cached, the style change only takes
                // effect once SetWindowPos runs.
                N.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, N.SWP_STYLE);
                addedLayered = true;
            }

            // LWA_ALPHA only. LWA_COLORKEY is known-broken on Chromium with GPU acceleration on.
            bool ok = N.SetLayeredWindowAttributes(h, 0, alpha, N.LWA_ALPHA);
            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();

                // Roll the style back. A window left WS_EX_LAYERED with no alpha ever set is not
                // tracked in _managed, so nothing would ever restore it -- and on some Windows
                // versions that state renders the window invisible.
                if (addedLayered)
                {
                    N.SetExStyle(h, ex);
                    N.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, N.SWP_STYLE);
                    N.RedrawWindow(h, IntPtr.Zero, IntPtr.Zero, N.RDW_FULL);
                }
                Log.W(string.Format("SetLayeredWindowAttributes FAILED hwnd=0x{0:X} err={1} rolledBack={2}",
                    h.ToInt64(), err, addedLayered));

                if (err == N.ERROR_ACCESS_DENIED && !_accessDeniedWarned)
                {
                    _accessDeniedWarned = true;
                    _tray.ShowBalloonTip(7000, "PipDimmer",
                        "無法調整這個視窗：它以較高的權限執行（UIPI 阻擋）。" +
                        "若目標是以系統管理員身分開啟的瀏覽器，請改用一般權限開啟。",
                        ToolTipIcon.Warning);
                }
                return;
            }

            if (_cfg.ForceRedraw)
                N.RedrawWindow(h, IntPtr.Zero, IntPtr.Zero, N.RDW_FULL);

            _managed[h] = alpha;
        }

        private void OnGhostToggle(IntPtr h)
        {
            if (h == IntPtr.Zero || !N.IsWindow(h)) return;

            int ex = N.GetExStyle(h);
            bool turnOn = (ex & N.WS_EX_TRANSPARENT) == 0;

            // WS_EX_TRANSPARENT alone is enough for click-through. Deliberately NOT adding
            // WS_EX_LAYERED here: a window made layered without a matching
            // SetLayeredWindowAttributes call can end up invisible.
            N.SetExStyle(h, turnOn ? (ex | N.WS_EX_TRANSPARENT) : (ex & ~N.WS_EX_TRANSPARENT));
            N.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, N.SWP_STYLE);

            if (turnOn) _ghosts.Add(h); else _ghosts.Remove(h);
            RebuildGhosts();

            FlashText(turnOn
                ? "滑鼠穿透：開（點擊會穿過去）"
                : "滑鼠穿透：關");
            MaskWinKeyIfNeeded();
        }

        private void RebuildGhosts()
        {
            // Rebuilt in EnumWindows order so the hook's rect test resolves the topmost ghost first.
            List<IntPtr> live = new List<IntPtr>();
            foreach (IntPtr h in _ghosts)
                if (N.IsWindow(h)) live.Add(h);
            Hook.Ghosts = live.ToArray();
        }

        private void RestoreAll()
        {
            List<IntPtr> managed = new List<IntPtr>(_managed.Keys);
            foreach (IntPtr h in managed)
                if (N.IsWindow(h)) ApplyAlpha(h, 255);
            _managed.Clear();

            List<IntPtr> ghosts = new List<IntPtr>(_ghosts);
            foreach (IntPtr h in ghosts)
            {
                if (!N.IsWindow(h)) continue;
                int ex = N.GetExStyle(h);
                if ((ex & N.WS_EX_TRANSPARENT) != 0)
                {
                    N.SetExStyle(h, ex & ~N.WS_EX_TRANSPARENT);
                    N.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, N.SWP_STYLE);
                }
            }
            _ghosts.Clear();
            RebuildGhosts();
            UpdateStatus();
        }

        #endregion

        #region scanning

        private bool EnumCallback(IntPtr h, IntPtr lParam)
        {
            if (W.IsPipWindow(h, _ownPid)) _tmpPips.Add(h);
            if (_ghosts.Contains(h)) _tmpGhosts.Add(h);
            return true;
        }

        private void Scan()
        {
            _tmpPips = new HashSet<IntPtr>();
            _tmpGhosts = new List<IntPtr>();
            N.EnumWindows(_enumProc, IntPtr.Zero);

            HashSet<IntPtr> pips = _tmpPips;
            Hook.PipSet = pips;
            Hook.Ghosts = _tmpGhosts.ToArray();   // EnumWindows order == z-order, top first

            // Drop dead handles so the dictionary cannot grow without bound.
            List<IntPtr> dead = null;
            foreach (IntPtr h in _managed.Keys)
            {
                if (!N.IsWindow(h))
                {
                    if (dead == null) dead = new List<IntPtr>();
                    dead.Add(h);
                }
            }
            if (dead != null) foreach (IntPtr h in dead) _managed.Remove(h);

            List<IntPtr> deadGhosts = null;
            foreach (IntPtr h in _ghosts)
            {
                if (!N.IsWindow(h))
                {
                    if (deadGhosts == null) deadGhosts = new List<IntPtr>();
                    deadGhosts.Add(h);
                }
            }
            if (deadGhosts != null) foreach (IntPtr h in deadGhosts) _ghosts.Remove(h);

            // The first scan only seeds the baseline. Windows that were already open when
            // PipDimmer started are not "new", so launching the tool changes nothing on screen.
            if (!_firstScan && _cfg.AutoApply && _cfg.LastAlphaPercent < 100)
            {
                byte want = W.PctToAlpha(_cfg.LastAlphaPercent);
                foreach (IntPtr h in pips)
                {
                    if (_knownPips.Contains(h)) continue;   // only brand-new PiP windows
                    if (_managed.ContainsKey(h)) continue;
                    ApplyAlpha(h, want);
                }
            }
            _firstScan = false;

            _knownPips = pips;
            UpdateStatus();

            if (Log.On)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("scan pips=").Append(pips.Count)
                  .Append(" managed=").Append(_managed.Count)
                  .Append(" ghosts=").Append(_ghosts.Count)
                  .Append(" hook=").Append(Hook.IsInstalled)
                  .Append(" cb=").Append(Hook.CbTotal)
                  .Append(" wheel=").Append(Hook.CbWheel)
                  .Append(" swallowed=").Append(Hook.CbSwallowed)
                  .Append(" lastTarget=0x").Append(Hook.LastTarget.ToInt64().ToString("X"))
                  .Append(" lastResult=").Append(Hook.LastResultCode);
                foreach (IntPtr h in pips)
                    sb.Append(" | pip=0x").Append(h.ToInt64().ToString("X"))
                      .Append(" ex=0x").Append(N.GetExStyle(h).ToString("X8"));
                Log.W(sb.ToString());
            }
        }

        private void OnWinEvent(IntPtr hHook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (hwnd == IntPtr.Zero || idObject != N.OBJID_WINDOW || idChild != 0) return;
            // EVENT_OBJECT_SHOW/HIDE fire constantly for menus and tooltips; the class check is the
            // cheap filter that keeps this callback trivial.
            if (W.ClassOf(hwnd) != W.ChromiumClass) return;
            _scanSoon.Stop();
            _scanSoon.Start();
        }

        #endregion

        #region ui feedback

        private void ShowAlphaOsd(IntPtr h, byte alpha)
        {
            if (!_cfg.ShowOsd) return;
            N.POINT p;
            if (!N.GetCursorPos(out p)) return;
            bool isPip = Hook.PipSet.Contains(h);
            string label = isPip ? "子母畫面" : TrimTitle(W.TextOf(h));
            if (_ghosts.Contains(h)) label = label + "（穿透中）";
            _osd.Flash(label, W.AlphaToPct(alpha), true, new Point(p.X, p.Y));
        }

        private void FlashText(string text)
        {
            if (!_cfg.ShowOsd) { _tray.Text = Trim63("PipDimmer — " + text); return; }
            N.POINT p;
            if (!N.GetCursorPos(out p)) return;
            _osd.Flash(text, 0, false, new Point(p.X, p.Y));
        }

        private static string TrimTitle(string t)
        {
            if (string.IsNullOrEmpty(t)) return "視窗";
            if (t.Length > 22) return t.Substring(0, 21) + "…";
            return t;
        }

        private static string Trim63(string s)
        {
            if (s == null) return "";
            return s.Length <= 63 ? s : s.Substring(0, 62) + "…";
        }

        private void UpdateStatus()
        {
            int pipCount = Hook.PipSet.Count;
            string s;
            if (!_cfg.Enabled) s = "已停用";
            else if (pipCount == 0) s = "尚未偵測到子母畫面";
            else
            {
                int shown = 0;
                string detail = "";
                foreach (IntPtr h in Hook.PipSet)
                {
                    byte a;
                    int pct = _managed.TryGetValue(h, out a) ? W.AlphaToPct(a) : 100;
                    if (shown > 0) detail += "、";
                    detail += pct.ToString(CultureInfo.InvariantCulture) + "%";
                    shown++;
                    if (shown >= 3) break;
                }
                s = "子母畫面 " + pipCount.ToString(CultureInfo.InvariantCulture) + " 個：" + detail;
            }
            _statusText = s;
            if (_miStatus != null) _miStatus.Text = "目前：" + s;
            _tray.Text = Trim63("PipDimmer — " + s);
        }

        #endregion

        #region diagnostics

        private void StartDiagnostics()
        {
            _countdownLeft = 3;
            FlashText("3 秒後擷取游標下的視窗…");
            _countdown.Start();
        }

        private void CountdownTick(object sender, EventArgs e)
        {
            _countdownLeft--;
            if (_countdownLeft > 0)
            {
                FlashText(_countdownLeft.ToString(CultureInfo.InvariantCulture) + " …");
                return;
            }
            _countdown.Stop();
            ShowWindowInfo();
        }

        private void ShowWindowInfo()
        {
            N.POINT p;
            if (!N.GetCursorPos(out p)) return;
            IntPtr h = N.WindowFromPoint(p);
            if (h != IntPtr.Zero) h = N.GetAncestor(h, N.GA_ROOT);
            if (h == IntPtr.Zero)
            {
                MessageBox.Show("游標下沒有視窗。", "PipDimmer", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            uint pid;
            N.GetWindowThreadProcessId(h, out pid);
            N.RECT r;
            N.GetWindowRect(h, out r);
            int style = N.GetStyle(h);
            int ex = N.GetExStyle(h);
            string proc = W.ProcessNameOf(pid);
            bool isPip = W.IsPipWindow(h, _ownPid);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("游標位置    : " + p.X + ", " + p.Y);
            sb.AppendLine("HWND        : 0x" + h.ToInt64().ToString("X"));
            sb.AppendLine("類別        : " + W.ClassOf(h));
            sb.AppendLine("標題        : " + W.TextOf(h));
            sb.AppendLine("程序        : " + proc + "  (PID " + pid + ")");
            sb.AppendLine("style       : 0x" + style.ToString("X8"));
            sb.AppendLine("exstyle     : 0x" + ex.ToString("X8"));
            sb.AppendLine("位置大小    : (" + r.Left + "," + r.Top + ") " + (r.Right - r.Left) + "x" + (r.Bottom - r.Top));
            sb.AppendLine();
            sb.AppendLine("TOPMOST     : " + (((ex & N.WS_EX_TOPMOST) != 0) ? "是" : "否"));
            sb.AppendLine("LAYERED     : " + (((ex & N.WS_EX_LAYERED) != 0) ? "是" : "否"));
            sb.AppendLine("TRANSPARENT : " + (((ex & N.WS_EX_TRANSPARENT) != 0) ? "是（滑鼠穿透）" : "否"));
            sb.AppendLine("目前透明度  : " + W.AlphaToPct(W.CurrentAlphaOf(h)) + "%");
            sb.AppendLine();
            sb.AppendLine("判定為子母畫面：" + (isPip ? "是（滾輪可直接調整）" : "否（需按住 Ctrl 才能調整）"));
            if (!isPip)
            {
                sb.AppendLine();
                sb.AppendLine("未判定為子母畫面的可能原因：");
                if (W.ClassOf(h) != W.ChromiumClass)
                    sb.AppendLine("  · 視窗類別不是 " + W.ChromiumClass);
                if (!W.IsBrowserProcess(proc))
                    sb.AppendLine("  · 程序 \"" + proc + "\" 不在支援的瀏覽器清單中");
                if ((ex & N.WS_EX_TOPMOST) == 0)
                    sb.AppendLine("  · 視窗沒有置頂（WS_EX_TOPMOST），且標題也不符合已知的子母畫面標題");
                if (W.RequirePipShape && W.HasBothCaptionBoxes(style) && !W.LooksLikePipTitle(W.TextOf(h)))
                    sb.AppendLine("  · 視窗同時有最小化與最大化鈕，形狀像一般瀏覽器視窗而不是子母畫面。" +
                                  "若這確實是子母畫面，請在 settings.ini 設 RequirePipShape=0");
            }

            MessageBox.Show(sb.ToString(), "PipDimmer — 視窗資訊",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        #endregion

        #region lifecycle

        private void WatchdogTick(object sender, EventArgs e)
        {
            if (!_cfg.Enabled) return;
            // Windows silently unhooks a low-level hook that times out too often and gives the app
            // no way to detect it. Proxy: the cursor moved but the callback has gone quiet.
            N.POINT p;
            if (!N.GetCursorPos(out p)) return;
            bool moved = (p.X != _wdLastX || p.Y != _wdLastY);
            _wdLastX = p.X;
            _wdLastY = p.Y;
            if (!moved) return;
            if (Environment.TickCount - Hook.LastTick > 3000 || !Hook.IsInstalled)
                Hook.Reinstall();
        }
        private int _wdLastX, _wdLastY;

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock ||
                e.Reason == SessionSwitchReason.SessionLogon ||
                e.Reason == SessionSwitchReason.RemoteConnect)
                Hook.Reinstall();
        }

        private void OnDisplayChanged(object sender, EventArgs e)
        {
            Hook.Reinstall();
            Scan();
        }

        private void OnSessionEnding(object sender, SessionEndingEventArgs e)
        {
            Cleanup();
        }

        private bool _cleaned;

        private void Cleanup()
        {
            if (_cleaned) return;
            _cleaned = true;

            try { RestoreAll(); } catch { }
            try { _cfg.Save(); } catch { }

            Hook.Uninstall();
            if (_winEventHook != IntPtr.Zero) { N.UnhookWinEvent(_winEventHook); _winEventHook = IntPtr.Zero; }
            if (_winEventPin.IsAllocated) _winEventPin.Free();

            _scan.Stop(); _scanSoon.Stop(); _watchdog.Stop(); _saveSoon.Stop(); _countdown.Stop();

            try
            {
                _tray.Visible = false;
                if (_tray.Icon != null) { _tray.Icon.Dispose(); _tray.Icon = null; }
                if (_iconHandle != IntPtr.Zero) { N.DestroyIcon(_iconHandle); _iconHandle = IntPtr.Zero; }
                _tray.Dispose();
            }
            catch { }

            try { _osd.Dispose(); } catch { }

            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.SessionEnding -= OnSessionEnding;
            SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        }

        private void ExitApp()
        {
            Cleanup();
            ExitThread();
        }

        private Icon BuildIcon()
        {
            Bitmap bmp = new Bitmap(32, 32);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush back = new SolidBrush(Color.FromArgb(255, 34, 40, 52)))
                using (GraphicsPath p = RoundRect(new Rectangle(1, 1, 29, 29), 7))
                    g.FillPath(back, p);
                using (Pen edge = new Pen(Color.FromArgb(255, 96, 172, 255), 1.6f))
                using (GraphicsPath p = RoundRect(new Rectangle(1, 1, 29, 29), 7))
                    g.DrawPath(edge, p);
                // the semi-transparent "child" pane
                using (SolidBrush pane = new SolidBrush(Color.FromArgb(140, 235, 240, 255)))
                    g.FillRectangle(pane, 14, 15, 13, 10);
                using (Pen paneEdge = new Pen(Color.FromArgb(230, 235, 240, 255), 1.2f))
                    g.DrawRectangle(paneEdge, 14, 15, 13, 10);
            }
            _iconHandle = bmp.GetHicon();
            Icon ico = Icon.FromHandle(_iconHandle);
            bmp.Dispose();
            return ico;
        }

        private static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath p = new GraphicsPath();
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        #endregion
    }

    #endregion

    #region ---------- Autostart ----------

    internal static class Autostart
    {
        private const string LinkName = "PipDimmer.lnk";
        private const string ApprovedKey =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

        private static string LinkPath()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), LinkName);
        }

        private static string ExePath()
        {
            Assembly a = Assembly.GetEntryAssembly();
            if (a != null && !string.IsNullOrEmpty(a.Location)) return a.Location;
            return Application.ExecutablePath;
        }

        public static bool IsEnabled()
        {
            if (!File.Exists(LinkPath())) return false;
            return !IsDisabledByTaskManager();
        }

        // Task Manager's "Startup apps" tab does not delete the shortcut; it records the disabled
        // state here (first byte 0x02 = enabled, 0x03 = disabled). Without reading it the tray
        // checkbox would claim autostart works when it does not.
        public static bool IsDisabledByTaskManager()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(ApprovedKey))
                {
                    if (k == null) return false;
                    byte[] v = k.GetValue(LinkName) as byte[];
                    if (v == null || v.Length == 0) return false;
                    return (v[0] & 0x01) != 0;
                }
            }
            catch { return false; }
        }

        public static string Set(bool enable)
        {
            try
            {
                string link = LinkPath();
                if (!enable)
                {
                    if (File.Exists(link)) File.Delete(link);
                    return null;
                }

                string exe = ExePath();
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
                    return "找不到執行檔路徑。";

                // Late-bound WScript.Shell: writes a real .lnk without the IShellLink COM interop
                // boilerplate, which is awkward under C# 5.
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                if (t == null) return "系統沒有 WScript.Shell。";
                object shell = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell,
                    new object[] { link });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { exe });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc,
                    new object[] { Path.GetDirectoryName(exe) });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, sc,
                    new object[] { "PipDimmer — 子母畫面透明控制" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }

    #endregion

    #region ---------- Entry point ----------

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], "-log", StringComparison.OrdinalIgnoreCase)) Log.Enable();

            // The manifest already declares PerMonitorV2; this is belt-and-braces for the case
            // where the exe is launched in a way that bypasses it. Failure here is expected and fine.
            try { N.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch { try { N.SetProcessDpiAwareness(2); } catch { } }

            bool created;
            using (Mutex mutex = new Mutex(true, "PipDimmer.SingleInstance.6C2F1A4B", out created))
            {
                if (!created)
                {
                    MessageBox.Show("PipDimmer 已經在執行中，請看系統匣圖示。", "PipDimmer",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayApp());
                GC.KeepAlive(mutex);
            }
        }
    }

    #endregion
}
