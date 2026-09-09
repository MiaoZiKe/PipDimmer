# PipFixture.ps1 -- opens a real Chrome Picture-in-Picture window on demand.
#
# Dot-source this, then call Start-PipFixture (returns the PiP window handle) and
# Stop-PipFixture. Chrome runs with a throwaway --user-data-dir at a fixed geometry so the
# harness knows where to click, and Stop-PipFixture only ever kills processes whose command
# line names that directory -- never the user's own browsing session.
#
# ASCII-only on purpose: Windows PowerShell 5.1 reads a .ps1 without a UTF-8 BOM using the
# system ANSI codepage, which turns non-ASCII characters into mojibake and breaks parsing.

$script:FixtureHtml = Join-Path $PSScriptRoot 'pip_fixture.html'
$script:ProfileDir  = Join-Path $env:TEMP 'PipDimmerTestProfile'
$script:FixtureProc = $null

function Find-Chrome {
    $candidates = @(
        (Join-Path $env:ProgramFiles 'Google\Chrome\Application\chrome.exe')
        (Join-Path ${env:ProgramFiles(x86)} 'Google\Chrome\Application\chrome.exe')
        (Join-Path $env:LOCALAPPDATA 'Google\Chrome\Application\chrome.exe')
    )
    foreach ($c in $candidates) { if ($c -and (Test-Path $c)) { return $c } }
    throw "chrome.exe not found. The fixture needs Google Chrome installed."
}

$fixCode = @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class Fix {
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
  delegate bool EnumWindowsProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWLP(IntPtr h, int i);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern bool GetCursorPos(out PT p);
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint n, INPUT[] i, int cb);
  [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr c);

  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  [StructLayout(LayoutKind.Sequential)] public struct PT { public int X,Y; }
  [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx,dy; public uint mouseData,dwFlags,time; public IntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }

  const int WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x08000000;
  const int WS_MINIMIZE = 0x20000000;

  static List<IntPtr> _found = new List<IntPtr>();
  static uint _wantPid;
  static bool _wantTopmost;

  static bool Cb(IntPtr h, IntPtr l) {
    if (!IsWindowVisible(h)) return true;
    uint pid; GetWindowThreadProcessId(h, out pid);
    if (pid != _wantPid) return true;
    var cb = new StringBuilder(256); GetClassNameW(h, cb, 256);
    if (cb.ToString() != "Chrome_WidgetWin_1") return true;
    if (((int)(long)GetWLP(h, -16) & WS_MINIMIZE) != 0) return true;
    int ex = (int)(long)GetWLP(h, -20);
    if ((ex & WS_EX_NOACTIVATE) != 0) return true;
    if (((ex & WS_EX_TOPMOST) != 0) != _wantTopmost) return true;
    RECT r; GetWindowRect(h, out r);
    if ((r.R - r.L) < 60 || (r.B - r.T) < 40) return true;
    _found.Add(h);
    return true;
  }

  // Chrome spreads windows across several processes, so callers pass every candidate pid.
  public static IntPtr Find(uint[] pids, bool topmost) {
    try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch {}
    _wantTopmost = topmost;
    foreach (uint pid in pids) {
      _found.Clear(); _wantPid = pid;
      EnumWindows(new EnumWindowsProc(Cb), IntPtr.Zero);
      if (_found.Count > 0) return _found[0];
    }
    return IntPtr.Zero;
  }

  public static string Describe(IntPtr h) {
    if (h == IntPtr.Zero) return "(none)";
    var cb = new StringBuilder(256); GetClassNameW(h, cb, 256);
    var tb = new StringBuilder(512); GetWindowTextW(h, tb, 512);
    RECT r; GetWindowRect(h, out r);
    return string.Format("0x{0:X} class={1} style=0x{2:X8} ex=0x{3:X8} rect=({4},{5})-({6},{7}) {8}x{9} title=[{10}]",
      h.ToInt64(), cb.ToString(), (int)(long)GetWLP(h,-16), (int)(long)GetWLP(h,-20),
      r.L, r.T, r.R, r.B, r.R-r.L, r.B-r.T, tb.ToString());
  }

  public static string TitleOf(IntPtr h) {
    var tb = new StringBuilder(512);
    int n = GetWindowTextW(h, tb, 512);
    return n > 0 ? tb.ToString(0, n) : "";
  }

  public static void ClickCentre(IntPtr h) {
    RECT r; GetWindowRect(h, out r);
    SetForegroundWindow(h);
    System.Threading.Thread.Sleep(400);
    SetCursorPos((r.L+r.R)/2, (r.T+r.B)/2);
    System.Threading.Thread.Sleep(250);
    INPUT[] i = new INPUT[2];
    i[0].mi.dwFlags = 0x0002;   // LEFTDOWN
    i[1].mi.dwFlags = 0x0004;   // LEFTUP
    SendInput(2, i, Marshal.SizeOf(typeof(INPUT)));
  }

  public static PT Cursor() { PT p; GetCursorPos(out p); return p; }
  public static void PutCursor(int x, int y) { SetCursorPos(x, y); }
}
'@
if (-not ('Fix' -as [type])) { Add-Type -TypeDefinition $fixCode -Language CSharp }

# Restrict every process lookup to the throwaway profile. Get-Process has no CommandLine in
# PS 5.1, so this must go through CIM -- matching on the image name alone would sweep up the
# user's real Chrome.
function Get-FixturePids {
    $p = Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" -ErrorAction SilentlyContinue |
         Where-Object { $_.CommandLine -and $_.CommandLine.Contains($script:ProfileDir) }
    return @($p | ForEach-Object { [uint32]$_.ProcessId })
}

function Start-PipFixture {
    $chrome = Find-Chrome
    if (-not (Test-Path $script:FixtureHtml)) { throw "missing fixture page: $($script:FixtureHtml)" }

    $chromeArgs = @(
        "--user-data-dir=$script:ProfileDir"
        '--no-first-run'
        '--no-default-browser-check'
        '--disable-features=Translate,OptimizationGuideModelDownloading'
        '--window-position=120,120'
        '--window-size=760,520'
        "--app=file:///$($script:FixtureHtml -replace '\\','/')"
    )
    $script:FixtureProc = Start-Process -FilePath $chrome -ArgumentList $chromeArgs -PassThru
    Start-Sleep -Seconds 5

    $page = [Fix]::Find((Get-FixturePids), $false)
    $tries = 0
    while ($page -eq [IntPtr]::Zero -and $tries -lt 10) {
        Start-Sleep -Milliseconds 700
        $page = [Fix]::Find((Get-FixturePids), $false)
        $tries++
    }
    if ($page -eq [IntPtr]::Zero) { throw "fixture page window never appeared" }
    Write-Host ("fixture page  : " + [Fix]::Describe($page))

    $saved = [Fix]::Cursor()
    $pip = [IntPtr]::Zero
    # SetForegroundWindow from a background process often no-ops, so the first click may only
    # activate the window. Click a few times and read document.title for the verdict.
    for ($attempt = 1; $attempt -le 4 -and $pip -eq [IntPtr]::Zero; $attempt++) {
        [Fix]::ClickCentre($page)
        Start-Sleep -Milliseconds 1500
        Write-Host ("  attempt $attempt title=[" + [Fix]::TitleOf($page) + "]")
        $pip = [Fix]::Find((Get-FixturePids), $true)
        if ($pip -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 700; $pip = [Fix]::Find((Get-FixturePids), $true) }
    }
    [Fix]::PutCursor($saved.X, $saved.Y)

    if ($pip -eq [IntPtr]::Zero) {
        throw ("PiP window never appeared; page title was [" + [Fix]::TitleOf($page) + "]")
    }
    Write-Host ("fixture PiP   : " + [Fix]::Describe($pip))
    return $pip
}

function Stop-PipFixture {
    foreach ($v in (Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" -ErrorAction SilentlyContinue |
                    Where-Object { $_.CommandLine -and $_.CommandLine.Contains($script:ProfileDir) })) {
        try { Stop-Process -Id $v.ProcessId -Force -ErrorAction SilentlyContinue } catch {}
    }
    Start-Sleep -Milliseconds 1000
    $left = @(Get-CimInstance Win32_Process -Filter "Name='chrome.exe'" -ErrorAction SilentlyContinue |
              Where-Object { $_.CommandLine -and $_.CommandLine.Contains($script:ProfileDir) })
    Write-Host ("fixture chrome processes left: " + $left.Count)
    Remove-Item $script:ProfileDir -Recurse -Force -ErrorAction SilentlyContinue
}
