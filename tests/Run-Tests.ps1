# Run-Tests.ps1 -- end-to-end suite for both PipDimmer editions.
#
#   .\tests\Run-Tests.ps1              both editions
#   .\tests\Run-Tests.ps1 -Only cs     C# executable only
#   .\tests\Run-Tests.ps1 -Only ahk    AutoHotkey script only
#
# The suite drives real mouse input, so it parks the cursor on the fixture windows and puts it
# back afterwards. It never touches windows the user owns: the PiP window comes from a
# throwaway Chrome profile and the "other window" target is a Form this script creates.
#
# ASCII-only on purpose -- see the note at the top of PipFixture.ps1.

[CmdletBinding()]
param(
    [ValidateSet('both', 'cs', 'ahk')]
    [string]$Only = 'both',
    [string]$AhkExePath
)

$ErrorActionPreference = 'Stop'
$root   = Split-Path -Parent $PSScriptRoot
$csExe  = Join-Path $root 'PipDimmer.exe'
$ahkScr = Join-Path $root 'ahk\PipDimmer.ahk'
$log    = Join-Path $env:APPDATA 'PipDimmer\log.txt'
$ini    = Join-Path $env:APPDATA 'PipDimmer\settings.ini'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
. (Join-Path $PSScriptRoot 'PipFixture.ps1')

function Find-AutoHotkey {
    if ($AhkExePath) {
        if (-not (Test-Path -LiteralPath $AhkExePath -PathType Leaf)) {
            throw "AutoHotkey executable not found at $AhkExePath"
        }
        return (Resolve-Path -LiteralPath $AhkExePath).Path
    }
    $c = @(
        (Join-Path $env:ProgramFiles 'AutoHotkey\v2\AutoHotkey64.exe')
        (Join-Path $env:LOCALAPPDATA 'Programs\AutoHotkey\v2\AutoHotkey64.exe')
    )
    foreach ($p in $c) { if ($p -and (Test-Path $p)) { return $p } }
    return $null
}

# Check before changing settings, opening fixture windows, or sending input.
if ($Only -ne 'ahk' -and -not (Test-Path -LiteralPath $csExe -PathType Leaf)) {
    throw "PipDimmer.exe not found at $csExe -- run build.ps1 first"
}
$ahkExe = if ($Only -ne 'cs') { Find-AutoHotkey } else { $null }
if ($Only -eq 'ahk' -and -not $ahkExe) {
    throw 'AutoHotkey v2 is required for -Only ahk; install it or specify -AhkExePath.'
}
$running = @(Get-CimInstance Win32_Process -Filter "Name='PipDimmer.exe' OR Name LIKE 'AutoHotkey%.exe'" |
    Where-Object { $_.Name -eq 'PipDimmer.exe' -or $_.CommandLine -match 'PipDimmer\.ahk(?:["\s]|$)' })
if ($running.Count) {
    throw "PipDimmer is already running (PID $($running.ProcessId -join ', ')). Close it before running the suite."
}

$harness = @'
using System;
using System.Text;
using System.Threading;
using System.Drawing;
using System.Windows.Forms;
using System.Runtime.InteropServices;

public class H {
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWLP(IntPtr h, int i);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool GetCursorPos(out PT p);
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(PT p);
  [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, int f);
  [DllImport("user32.dll")] static extern bool GetLayeredWindowAttributes(IntPtr h, out uint k, out byte a, out uint f);
  [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint n, INPUT[] i, int cb);
  [DllImport("user32.dll")] static extern void keybd_event(byte k, byte s, uint f, UIntPtr e);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr FindWindowExW(IntPtr p, IntPtr a, string c, string t);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool PostMessageW(IntPtr h, int m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr c);

  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [StructLayout(LayoutKind.Sequential)] public struct PT { public int X,Y; }
  [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx,dy; public uint mouseData,dwFlags,time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }

  const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;

  static Form _form;
  public static IntPtr FormHandle = IntPtr.Zero;

  // A window this process owns, used as the "some other window" target so the suite never
  // has to touch anything the user is actually working in.
  public static void StartProbe(int x, int y) {
    try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch {}
    var ready = new ManualResetEvent(false);
    var t = new Thread(delegate() {
      _form = new Form();
      _form.Text = "PipDimmer test target";
      _form.StartPosition = FormStartPosition.Manual;
      _form.Location = new Point(x, y);
      _form.Size = new Size(400, 260);
      _form.BackColor = Color.Teal;
      _form.TopMost = true;
      _form.Shown += delegate { FormHandle = _form.Handle; ready.Set(); };
      Application.Run(_form);
    });
    t.SetApartmentState(ApartmentState.STA); t.IsBackground = true; t.Start();
    ready.WaitOne(8000); Thread.Sleep(400);
    Raise(x, y);
  }

  // Other topmost windows (a PowerPoint slideshow, a media player) will otherwise sit above
  // the probe, the cursor lands on them instead, and every measurement silently becomes
  // meaningless. Raising and verifying is what keeps failures honest.
  public static void Raise(int x, int y) {
    ShowWindow(FormHandle, 8);                                             // SW_SHOWNA
    SetWindowPos(FormHandle, new IntPtr(-1), x, y, 400, 260, 0x0010);      // HWND_TOPMOST, NOACTIVATE
    BringWindowToTop(FormHandle);
    SetForegroundWindow(FormHandle);
    Thread.Sleep(500);
  }
  public static void StopProbe() { try { _form.Invoke(new Action(delegate { _form.Close(); })); } catch {} }

  public static bool Park(IntPtr h) {
    RECT r; GetWindowRect(h, out r);
    SetCursorPos((r.L+r.R)/2, (r.T+r.B)/2);
    Thread.Sleep(250);
    PT p; GetCursorPos(out p);
    return GetAncestor(WindowFromPoint(p), 2) == h;
  }
  public static string Under() {
    PT p; GetCursorPos(out p);
    IntPtr h = GetAncestor(WindowFromPoint(p), 2);
    var cb = new StringBuilder(128); GetClassNameW(h, cb, 128);
    return string.Format("0x{0:X} [{1}]", h.ToInt64(), cb.ToString());
  }
  public static PT Cursor() { PT p; GetCursorPos(out p); return p; }
  public static void PutCursor(int x, int y) { SetCursorPos(x, y); }

  public static void Wheel(int n) {
    INPUT[] i = new INPUT[1];
    i[0].mi.dwFlags = 0x0800; i[0].mi.mouseData = unchecked((uint)(n*120));
    if (SendInput(1, i, Marshal.SizeOf(typeof(INPUT))) == 0)
      throw new Exception("SendInput failed, err=" + Marshal.GetLastWin32Error());
  }
  public static void MiddleClick() {
    INPUT[] i = new INPUT[2];
    i[0].mi.dwFlags = 0x0020; i[1].mi.dwFlags = 0x0040;
    SendInput(2, i, Marshal.SizeOf(typeof(INPUT)));
  }
  public static void CtrlDown()  { keybd_event(0x11,0,0,UIntPtr.Zero); }
  public static void CtrlUp()    { keybd_event(0x11,0,2,UIntPtr.Zero); }
  public static void ShiftDown() { keybd_event(0x10,0,0,UIntPtr.Zero); }
  public static void ShiftUp()   { keybd_event(0x10,0,2,UIntPtr.Zero); }

  public static int Alpha(IntPtr h) {
    int ex = (int)(long)GetWLP(h,-20);
    if ((ex & WS_EX_LAYERED) == 0) return 255;
    uint k; byte a; uint f;
    if (GetLayeredWindowAttributes(h, out k, out a, out f)) return a;
    return 255;
  }
  public static bool Ghosted(IntPtr h) { return (((int)(long)GetWLP(h,-20)) & WS_EX_TRANSPARENT) != 0; }
  public static string Ex(IntPtr h) { return string.Format("0x{0:X8}", (int)(long)GetWLP(h,-20)); }

  public static IntPtr CsMsgWindow(uint wantPid) {
    IntPtr h = IntPtr.Zero;
    while ((h = FindWindowExW(new IntPtr(-3), h, null, "PipDimmerMsgWindow")) != IntPtr.Zero) {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid == wantPid) return h;
    }
    return IntPtr.Zero;
  }
  public static IntPtr AhkWindow(uint wantPid) {
    IntPtr found = IntPtr.Zero;
    EnumWindows(delegate(IntPtr h, IntPtr l) {
      uint pid; GetWindowThreadProcessId(h, out pid);
      if (pid != wantPid) return true;
      var cb = new StringBuilder(64); GetClassNameW(h, cb, 64);
      if (cb.ToString() != "AutoHotkey") return true;
      var tb = new StringBuilder(512); GetWindowTextW(h, tb, 512);
      if (tb.ToString().IndexOf("PipDimmer.ahk", StringComparison.OrdinalIgnoreCase) >= 0) { found = h; return false; }
      return true;
    }, IntPtr.Zero);
    return found;
  }
  // Both editions quit cleanly on WM_CLOSE to their control window, which runs the same
  // restore path as the tray "quit" item.
  public static void CloseApp(IntPtr h) {
    if (h != IntPtr.Zero) PostMessageW(h, 0x0010, IntPtr.Zero, IntPtr.Zero);
  }
}
'@
if (-not ('H' -as [type])) {
    Add-Type -TypeDefinition $harness -Language CSharp -ReferencedAssemblies System.Windows.Forms, System.Drawing
}

$script:pass = 0
$script:fail = 0
function Check($name, $cond, $detail) {
    if ($cond) { $script:pass++; Write-Host "  [PASS] $name" -ForegroundColor Green }
    else       { $script:fail++; Write-Host "  [FAIL] $name" -ForegroundColor Red }
    if ($detail) { Write-Host "         $detail" -ForegroundColor DarkGray }
}
function Wheel($count, $dir = -1, $gap = 90) {
    for ($i = 0; $i -lt $count; $i++) { [H]::Wheel($dir); Start-Sleep -Milliseconds $gap }
}
function HookCounters {
    $line = Get-Content $log -ErrorAction SilentlyContinue |
            Where-Object { $_ -match 'wheel=(\d+) swallowed=(\d+)' } | Select-Object -Last 1
    if ($line -and $line -match 'wheel=(\d+) swallowed=(\d+)') {
        return @{ wheel = [int]$Matches[1]; swallowed = [int]$Matches[2] }
    }
    return $null
}

function Stop-TestApp {
    param([System.Diagnostics.Process]$Process, [string]$Edition, [switch]$Force)
    if (-not $Process -or $Process.HasExited) { return }
    $window = if ($Edition -eq 'cs') { [H]::CsMsgWindow($Process.Id) } else { [H]::AhkWindow($Process.Id) }
    [H]::CloseApp($window)
    if (-not $Process.WaitForExit(5000)) {
        if (-not $Force) { throw "$Edition edition did not exit after WM_CLOSE." }
        # The Process object identifies only the instance started by this test run.
        Stop-Process -InputObject $Process -Force -ErrorAction Stop
        if (-not $Process.WaitForExit(5000)) { throw "Could not stop test process $($Process.Id)." }
    }
}

function Invoke-Suite {
    param($Label, $Pip, $Probe, $ProbeX, $ProbeY, [scriptblock]$Start, [scriptblock]$Stop, [bool]$HasLog)

    Write-Host ""
    Write-Host "===== $Label =====" -ForegroundColor Cyan
    $pipExBefore = [H]::Ex($Pip)
    $probeExBefore = [H]::Ex($Probe)
    & $Start
    Start-Sleep -Seconds 3

    # ---------- PiP window: no modifier needed ----------
    if (-not [H]::Park($Pip)) { throw "cursor is not on the PiP window (it is on $([H]::Under()))" }

    $a0 = [H]::Alpha($Pip)
    Wheel 6
    Start-Sleep -Milliseconds 900
    $a1 = [H]::Alpha($Pip)
    Check "PiP: 6 notches = 6 steps of 13" (($a0 - $a1) -eq 78) "alpha $a0 -> $a1, expected $($a0 - 78)"

    Wheel 3 1
    Start-Sleep -Milliseconds 900
    $a2 = [H]::Alpha($Pip)
    Check "PiP: wheel up raises opacity" (($a2 - $a1) -eq 39) "alpha $a1 -> $a2"

    Wheel 30 -1 45
    Start-Sleep -Milliseconds 1200
    $a3 = [H]::Alpha($Pip)
    Check "PiP: clamps at the 10% floor" ($a3 -ge 25 -and $a3 -le 27) "alpha=$a3, expected 26"

    Wheel 40 1 40
    Start-Sleep -Milliseconds 1200
    Check "PiP: 100% restores original extended style" (([H]::Ex($Pip)) -eq $pipExBefore) "ex=$([H]::Ex($Pip)), expected $pipExBefore"

    # ---------- some other window: modifier gating ----------
    [H]::Raise($ProbeX, $ProbeY)
    if (-not [H]::Park($Probe)) { throw "cursor is not on the probe window (it is on $([H]::Under()))" }

    $c0 = if ($HasLog) { HookCounters } else { $null }
    $b0 = [H]::Alpha($Probe)
    [H]::CtrlDown(); Start-Sleep -Milliseconds 180
    Wheel 5
    Start-Sleep -Milliseconds 180; [H]::CtrlUp()
    Start-Sleep -Milliseconds 900
    $b1 = [H]::Alpha($Probe)
    Check "Ctrl+wheel is NOT captured (browser zoom survives)" ($b1 -eq 255) "alpha $b0 -> $b1"

    if ($HasLog -and $c0) {
        $c1 = HookCounters
        $seen = $c1.wheel - $c0.wheel
        $eaten = $c1.swallowed - $c0.swallowed
        # Alpha staying put only proves PipDimmer did not act. The hook's own counters prove
        # the event was handed on to the application rather than swallowed and dropped.
        Check "Ctrl+wheel is passed through to the app" ($seen -ge 5 -and $eaten -eq 0) `
              "hook saw $seen wheel events, swallowed $eaten"
    }

    $c1b = if ($HasLog) { HookCounters } else { $null }
    [H]::CtrlDown(); [H]::ShiftDown(); Start-Sleep -Milliseconds 220
    Wheel 5
    Start-Sleep -Milliseconds 220; [H]::ShiftUp(); [H]::CtrlUp()
    Start-Sleep -Milliseconds 900
    $b2 = [H]::Alpha($Probe)
    Check "Ctrl+Shift+wheel dims by 5 steps" (($b1 - $b2) -eq 65) "alpha $b1 -> $b2, expected $($b1 - 65)"

    if ($HasLog -and $c1b) {
        $c2 = HookCounters
        Check "Ctrl+Shift+wheel is captured by the hook" `
              ((($c2.wheel - $c1b.wheel) -ge 5) -and (($c2.swallowed - $c1b.swallowed) -ge 5)) `
              "hook saw $($c2.wheel - $c1b.wheel), swallowed $($c2.swallowed - $c1b.swallowed)"
    }

    # ---------- click-through ----------
    [H]::CtrlDown(); [H]::ShiftDown(); Start-Sleep -Milliseconds 220
    [H]::MiddleClick()
    Start-Sleep -Milliseconds 280; [H]::ShiftUp(); [H]::CtrlUp()
    Start-Sleep -Milliseconds 900
    Check "Ctrl+Shift+middle sets WS_EX_TRANSPARENT" ([H]::Ghosted($Probe)) "ex=$([H]::Ex($Probe))"

    $g0 = [H]::Alpha($Probe)
    [H]::CtrlDown(); [H]::ShiftDown(); Start-Sleep -Milliseconds 220
    Wheel 4 1
    Start-Sleep -Milliseconds 220; [H]::ShiftUp(); [H]::CtrlUp()
    Start-Sleep -Milliseconds 900
    $g1 = [H]::Alpha($Probe)
    # WindowFromPoint skips WS_EX_TRANSPARENT windows, so this only works because the app
    # keeps its own list of ghosted windows and resolves them by rectangle.
    Check "a ghosted window is still reachable by the wheel" ($g1 -ne $g0) "alpha $g0 -> $g1"

    # ---------- clean exit ----------
    Wheel 5
    Start-Sleep -Milliseconds 700
    & $Stop
    Start-Sleep -Seconds 2
    Check "clean exit restores every window it touched" `
          (([H]::Alpha($Probe)) -eq 255 -and ([H]::Ex($Probe)) -eq $probeExBefore -and ([H]::Ex($Pip)) -eq $pipExBefore) `
          "probe ex=$([H]::Ex($Probe))  pip ex=$([H]::Ex($Pip))"
}

# ---------------------------------------------------------------- run ------
# Preserve exact bytes (including encoding) and absence, even when a test fails.
$settingsBackup = @(foreach ($path in @($ini, $log)) {
    $existed = Test-Path -LiteralPath $path -PathType Leaf
    @{ Path = $path; Existed = $existed; Bytes = if ($existed) { ,([IO.File]::ReadAllBytes($path)) } else { $null } }
})
$script:csProcess = $null
$script:ahkProcess = $null

$probeX = 300; $probeY = 620
$saved = [H]::Cursor()
try {
    foreach ($path in @($ini, $log)) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
    }
    $pip = Start-PipFixture
    [H]::StartProbe($probeX, $probeY)
    $probe = [H]::FormHandle
    Write-Host ("probe window  : 0x{0:X}" -f $probe.ToInt64())

    if ($Only -eq 'both' -or $Only -eq 'cs') {
        Invoke-Suite -Label 'C# edition (PipDimmer.exe)' -Pip $pip -Probe $probe -ProbeX $probeX -ProbeY $probeY `
            -Start { $script:csProcess = Start-Process $csExe -ArgumentList '-log' -WindowStyle Hidden -PassThru } `
            -Stop  { Stop-TestApp $script:csProcess 'cs' } `
            -HasLog $true
        foreach ($path in @($ini, $log)) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
        }
    }

    if ($Only -eq 'both' -or $Only -eq 'ahk') {
        if (-not $ahkExe) {
            Write-Host ""
            Write-Host "===== AutoHotkey edition: SKIPPED (AutoHotkey v2 not installed) =====" -ForegroundColor Yellow
        } else {
            Invoke-Suite -Label 'AutoHotkey edition (PipDimmer.ahk)' -Pip $pip -Probe $probe -ProbeX $probeX -ProbeY $probeY `
                -Start { $script:ahkProcess = Start-Process $ahkExe -ArgumentList "/ErrorStdOut `"$ahkScr`"" -WindowStyle Hidden -PassThru } `
                -Stop  { Stop-TestApp $script:ahkProcess 'ahk' } `
                -HasLog $false
        }
    }
}
catch {
    Write-Host ("ERROR: " + $_.Exception.Message) -ForegroundColor Red
    $script:fail++
}
finally {
    [H]::ShiftUp()
    [H]::CtrlUp()
    foreach ($edition in @('cs', 'ahk')) {
        $testProcess = if ($edition -eq 'cs') { $script:csProcess } else { $script:ahkProcess }
        try { Stop-TestApp $testProcess $edition -Force }
        catch { $script:fail++; Write-Host "Cleanup failed: $($_.Exception.Message)" -ForegroundColor Red }
    }
    [H]::PutCursor($saved.X, $saved.Y)
    [H]::StopProbe()
    try { Stop-PipFixture }
    catch { $script:fail++; Write-Host "Fixture cleanup failed: $($_.Exception.Message)" -ForegroundColor Red }
    foreach ($backup in $settingsBackup) {
        try {
            if ($backup.Existed) { [IO.File]::WriteAllBytes($backup.Path, [byte[]]$backup.Bytes) }
            elseif (Test-Path -LiteralPath $backup.Path) { Remove-Item -LiteralPath $backup.Path -Force }
        }
        catch { $script:fail++; Write-Host "Could not restore $($backup.Path): $($_.Exception.Message)" -ForegroundColor Red }
    }
    Write-Host ""
    Write-Host "TOTAL: $($script:pass) passed, $($script:fail) failed" `
        -ForegroundColor $(if ($script:fail -eq 0) { 'Green' } else { 'Red' })
}

if ($script:fail -gt 0) { exit 1 }
