#Requires AutoHotkey v2.0
#SingleInstance Force
; ============================================================================
;  PipDimmer (AutoHotkey v2 edition)
;  Chrome 子母畫面滾輪透明控制器
;
;  游標移到子母畫面上 -> 直接滾滾輪調透明度
;  游標在其他視窗上   -> 修飾鍵+滾輪調透明度（預設 Ctrl+Shift，系統匣可改）
;  修飾鍵+中鍵        -> 切換滑鼠穿透（幽靈視窗）
;
;  與 C# 版共用同一份設定檔：%APPDATA%\PipDimmer\settings.ini
;  兩個版本請勿同時執行，否則兩邊都會去搶同一個視窗。
; ============================================================================

DetectHiddenWindows false
CoordMode "Mouse", "Screen"

; AHK's default is 1 thread per hotkey, which silently DISCARDS wheel notches that arrive
; while the previous one is still being handled. Combined with the deferred handler below
; (hotkey only queues, a timer does the work) this stops fast scrolling from losing steps.
#MaxThreadsPerHotkey 6
#MaxThreadsBuffer true

; ---------------------------------------------------------------- 設定 -----
global CfgPath := A_AppData "\PipDimmer\settings.ini"
global CFG := Map(
    "Enabled",           1,
    "StepPercent",       5,
    "MinPercent",        10,
    "LastAlphaPercent",  60,
    "ShowOsd",           1,
    "AutoApply",         1,
    "AllowOtherWindows", 1,
    "GhostGesture",      1,
    "InvertWheel",       0,
    "ForceRedraw",       0,
    "RequirePipShape",   1,
    ; 修飾鍵位元遮罩：1=Ctrl 2=Shift 4=Alt 8=Win，相加，0=不需修飾鍵。
    ; 預設不是單獨 Ctrl：Ctrl+滾輪是瀏覽器／VS Code／檔案總管的縮放鍵，
    ; Ctrl+中鍵則是「在新分頁開啟連結」，吃掉它們會很痛。
    "ModifierMask",      3)

; ---------------------------------------------------------------- 狀態 -----
global PipSet    := Map()   ; 由 Scan() 維護；#HotIf 只做 O(1) 查表
global KnownPips := Map()
global Managed   := Map()   ; hwnd -> alpha
global Ghosts    := Map()   ; hwnd -> true（已設 WS_EX_TRANSPARENT）
global FirstScan := true
global OsdHwnd   := 0

; Wheel notches are queued here by the hotkey and applied by a timer -- see Adjust().
; These must be declared up here in the auto-execute section: a `global x := 0` placed
; further down, after the hotkey definitions, never actually runs its initializer.
global PendingHwnd  := 0
global PendingDelta := 0

global OsdGui := 0, OsdLabel := 0, OsdPct := 0, OsdTrack := 0

; 修飾鍵熱鍵是動態註冊的（前綴會變），所以 #HotIf 條件必須是「同一個」函式物件，
; 否則 Hotkey ... "Off" 找不到要關掉的那組變體。
global HifAny      := (*) => CanTargetAny()
global HifGhostCtx := (*) => CanGhost()
; 處理函式同理：Hotkey ... "Off" 認的是變體，而變體綁在建立時的條件物件上。
global HkWheelUp   := (*) => Adjust(1)
global HkWheelDown := (*) => Adjust(-1)
global HkGhost     := (*) => ToggleGhost()
global RegisteredMod := ""       ; 目前已註冊的前綴（"" 也是合法值：不需修飾鍵）
global HasRegistered := false

; ------------------------------------------------------------- 常數 --------
global WS_MINIMIZE     := 0x20000000
global WS_MAXIMIZEBOX  := 0x00010000
global WS_MINIMIZEBOX  := 0x00020000
global EX_TOPMOST      := 0x00000008
global EX_TRANSPARENT  := 0x00000020
global EX_LAYERED      := 0x00080000
global EX_NOACTIVATE   := 0x08000000
global RDW_FULL        := 0x585        ; INVALIDATE|ERASE|FRAME|ALLCHILDREN|UPDATENOW
global SWP_STYLE       := 0x37         ; NOSIZE|NOMOVE|NOZORDER|NOACTIVATE|FRAMECHANGED

global BrowserProcs := ["chrome.exe", "msedge.exe", "brave.exe", "vivaldi.exe"
                      , "opera.exe", "opera_gx.exe", "chromium.exe", "thorium.exe"]

; 子母畫面標題只當備援：Document PiP（YouTube 迷你播放器）帶的是網頁標題，
; 而且這串字跟著瀏覽器的介面語言走，不是作業系統語言。
global PipTitles := ["子母畫面", "画中画", "畫中畫"
                   , "Picture in picture", "Picture in Picture", "Picture-in-Picture"
                   , "ピクチャー イン ピクチャー", "PIP 모드"
                   , "Bild im Bild", "Imagen en imagen", "Mode PIP (Picture-in-Picture)"]

global ShellClasses := ["Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"
                      , "NotifyIconOverflowWindow", "MultitaskingViewFrame"]

; ============================================================== 啟動 =======
; CI checks the real GUI/menu initialization without running the controller or reading/writing user settings.
if (A_Args.Length = 1 && A_Args[1] = "--self-test") {
    try {
        BuildOsd()
        BuildTray()
        FileAppend "PipDimmer AHK initialization OK`n", "*", "UTF-8"
    } catch as err {
        FileAppend "Initialization failed: " err.Message " (line " err.Line ")`n", "*", "UTF-8"
        ExitApp 1
    }
    ExitApp 0
}

LoadCfg()
BuildOsd()
BuildTray()
ApplyModHotkeys()
Scan()
SetTimer Scan, 1000
OnExit Cleanup
UpdateTip()

; ========================================================== 熱鍵 ===========
; AHK 的 #HotIf 天生就是「條件成立時熱鍵吃掉事件」，不必自己掛 WH_MOUSE_LL。
; 條件函式必須極輕：只做游標定位 + 一次 Map 查表。

; 子母畫面上不需修飾鍵，前綴固定，所以用靜態宣告。這組必須先宣告：AHK 依建立順序
; 評估同一個熱鍵的各個變體，讓子母畫面優先於「無修飾鍵」的其他視窗設定。
#HotIf PipUnderCursor()
WheelUp::   Adjust(1)
WheelDown:: Adjust(-1)
#HotIf

; 其他視窗的熱鍵前綴會隨設定改變，所以改用 Hotkey() 在執行期註冊，見 ApplyModHotkeys()。

; ============================================ 動態修飾鍵熱鍵 ===============
ModPrefix(mask) {
    p := ""
    if (mask & 1)
        p .= "^"
    if (mask & 2)
        p .= "+"
    if (mask & 4)
        p .= "!"
    if (mask & 8)
        p .= "#"
    return p
}

ModName(mask) {
    if (mask = 0)
        return "無"
    parts := []
    if (mask & 1)
        parts.Push("Ctrl")
    if (mask & 2)
        parts.Push("Shift")
    if (mask & 4)
        parts.Push("Alt")
    if (mask & 8)
        parts.Push("Win")
    return JoinArr(parts, "+")
}

ApplyModHotkeys() {
    global RegisteredMod, HasRegistered
    prefix := ModPrefix(CFG["ModifierMask"])
    if (HasRegistered && prefix = RegisteredMod)
        return

    ; 關掉舊的那一組。變體是由「目前的 HotIf 條件」決定的，所以要先把條件設回同一個
    ; 函式物件，否則會關到不存在的變體。省略 Action 參數才是只改選項。
    if HasRegistered {
        HotIf HifAny
        try Hotkey RegisteredMod "WheelUp", , "Off"
        try Hotkey RegisteredMod "WheelDown", , "Off"
        HotIf HifGhostCtx
        try Hotkey RegisteredMod "MButton", , "Off"
        HotIf
    }

    HotIf HifAny
    Hotkey prefix "WheelUp",   HkWheelUp,   "On"
    Hotkey prefix "WheelDown", HkWheelDown, "On"
    HotIf HifGhostCtx
    Hotkey prefix "MButton",   HkGhost,     "On"
    HotIf

    RegisteredMod := prefix
    HasRegistered := true
}

; ====================================================== 目標解析 ===========

; 幽靈視窗必須先用矩形找，因為 MouseGetPos 跟 WindowFromPoint 一樣會直接跳過
; WS_EX_TRANSPARENT 的視窗——不先做這步，一旦開了穿透就再也調不回來。
ResolveTarget() {
    MouseGetPos(&mx, &my, &win)
    for hwnd, _ in Ghosts {
        if !WinExist("ahk_id " hwnd)
            continue
        try {
            WinGetPos(&x, &y, &w, &h, "ahk_id " hwnd)
            if (mx >= x && mx < x + w && my >= y && my < y + h)
                return hwnd
        }
    }
    return (win && win != OsdHwnd) ? win : 0
}

PipUnderCursor() {
    if !CFG["Enabled"]
        return false
    hwnd := ResolveTarget()
    return hwnd && PipSet.Has(hwnd)
}

CanTargetAny() {
    if (!CFG["Enabled"] || !CFG["AllowOtherWindows"])
        return false
    hwnd := ResolveTarget()
    return hwnd && !IsShell(hwnd)
}

CanGhost() {
    if (!CFG["Enabled"] || !CFG["GhostGesture"])
        return false
    hwnd := ResolveTarget()
    return hwnd && !IsShell(hwnd)
}

IsShell(hwnd) {
    try cls := WinGetClass("ahk_id " hwnd)
    catch
        return true
    for c in ShellClasses
        if (cls = c)
            return true
    return false
}

; ====================================================== 子母畫面辨識 =======
; 判斷依據是量測出來的事實：子母畫面 ex=0x00200108（置頂）且 style=0x16CC0000
; （沒有最小化/最大化鈕）；一般 Chrome 視窗是 ex=0x00200100 / style=0x36CF0000。
IsPip(hwnd) {
    try {
        if (WinGetClass("ahk_id " hwnd) != "Chrome_WidgetWin_1")
            return false

        ex := WinGetExStyle("ahk_id " hwnd)
        ; Chromium 會生出一堆置頂的暫時性覆蓋層（提示泡泡、自動填入下拉），
        ; 它們是不可啟動的（WS_EX_NOACTIVATE）；真正的子母畫面點得下去。
        if (ex & EX_NOACTIVATE)
            return false

        st := WinGetStyle("ahk_id " hwnd)
        if (st & WS_MINIMIZE)
            return false

        WinGetPos(, , &w, &h, "ahk_id " hwnd)
        if (w < 120 || h < 80)
            return false

        topmost := (ex & EX_TOPMOST) != 0
        ; 一般瀏覽器視窗兩個鈕都有；子母畫面兩個都沒有。少了這道，被別的工具
        ; 設成置頂的一般 Chrome 視窗會被誤判，然後它的滾輪就被吃掉了。
        shaped  := !CFG["RequirePipShape"] || !((st & WS_MINIMIZEBOX) && (st & WS_MAXIMIZEBOX))
        titled  := IsPipTitle(WinGetTitle("ahk_id " hwnd))

        if !((topmost && shaped) || titled)
            return false

        proc := WinGetProcessName("ahk_id " hwnd)
        for p in BrowserProcs
            if (proc = p)
                return true
        return false
    } catch
        return false
}

IsPipTitle(t) {
    if (t = "")
        return false
    for s in PipTitles
        if (t = s)
            return true
    return false
}

; ====================================================== 透明度套用 =========
CurrentAlpha(hwnd) {
    if Managed.Has(hwnd)
        return Managed[hwnd]
    try {
        if !(WinGetExStyle("ahk_id " hwnd) & EX_LAYERED)
            return 255
    } catch
        return 255
    key := 0, a := 0, flags := 0
    if DllCall("GetLayeredWindowAttributes", "ptr", hwnd, "uint*", &key, "uchar*", &a, "uint*", &flags)
        && (flags & 2)
        return a
    return 255
}

ApplyAlpha(hwnd, alpha) {
    if (alpha >= 255) {
        try {
            ; 先設 255 再 Off——AHK 官方文件對 WinSetTransparent 的建議，
            ; 可避免重繪問題。Off 會清掉 WS_EX_LAYERED，讓視窗回到原生繪製路徑。
            WinSetTransparent 255, "ahk_id " hwnd
            WinSetTransparent "Off", "ahk_id " hwnd
            DllCall("RedrawWindow", "ptr", hwnd, "ptr", 0, "ptr", 0, "uint", RDW_FULL)
        }
        if Managed.Has(hwnd)
            Managed.Delete(hwnd)
        return
    }
    ; 只用 alpha，絕不用 WinSetTransColor（= LWA_COLORKEY，
    ; 在開硬體加速的 Chromium 上是壞的）。
    try WinSetTransparent alpha, "ahk_id " hwnd
    catch
        return
    if CFG["ForceRedraw"]
        DllCall("RedrawWindow", "ptr", hwnd, "ptr", 0, "ptr", 0, "uint", RDW_FULL)
    Managed[hwnd] := alpha
}

; The hotkey itself must return almost immediately, so it only records which window and how
; many notches, then hands off. Showing the OSD from inside the hotkey thread was slow enough
; that 5 of every 6 notches were being dropped.
Adjust(dir) {
    global PendingHwnd, PendingDelta
    hwnd := ResolveTarget()
    if !hwnd
        return
    if (hwnd != PendingHwnd) {
        PendingHwnd := hwnd
        PendingDelta := 0
    }
    PendingDelta += dir
    SetTimer ApplyPending, -1
}

ApplyPending() {
    global PendingHwnd, PendingDelta
    hwnd := PendingHwnd
    notches := PendingDelta
    PendingDelta := 0
    if (!hwnd || !notches || !WinExist("ahk_id " hwnd))
        return
    ApplyNotches(hwnd, notches)
}

ApplyNotches(hwnd, dir) {
    if CFG["InvertWheel"]
        dir := -dir

    cur  := CurrentAlpha(hwnd)
    step := Round(CFG["StepPercent"] * 255 / 100)
    if (step < 1)
        step := 1
    minA := Round(CFG["MinPercent"] * 255 / 100)
    if (minA < 26)          ; alpha 0 會讓視窗點不到、抓不回來
        minA := 26

    next := cur + dir * step
    if (next > 255)
        next := 255
    if (next < minA)
        next := minA

    ApplyAlpha(hwnd, next)
    CFG["LastAlphaPercent"] := Round(next * 100 / 255)
    SaveSoon()
    ShowOsd(hwnd, next)
    UpdateTip()
}

ToggleGhost() {
    hwnd := ResolveTarget()
    if (!hwnd || IsShell(hwnd))
        return
    try ex := WinGetExStyle("ahk_id " hwnd)
    catch
        return
    on := !(ex & EX_TRANSPARENT)
    ; 只加 WS_EX_TRANSPARENT 就足以穿透。刻意不一起加 WS_EX_LAYERED：
    ; 一個被設成 layered 卻沒設過 alpha 的視窗可能整個看不見。
    try WinSetExStyle(on ? "+" EX_TRANSPARENT : "-" EX_TRANSPARENT, "ahk_id " hwnd)
    catch
        return
    DllCall("SetWindowPos", "ptr", hwnd, "ptr", 0, "int", 0, "int", 0, "int", 0, "int", 0, "uint", SWP_STYLE)
    if on
        Ghosts[hwnd] := true
    else if Ghosts.Has(hwnd)
        Ghosts.Delete(hwnd)
    OsdMessage(on ? "滑鼠穿透：開" : "滑鼠穿透：關")
}

; ========================================================== 掃描 ===========
Scan() {
    global PipSet, KnownPips, FirstScan
    pips := Map()
    for hwnd in WinGetList()
        if IsPip(hwnd)
            pips[hwnd] := true

    ; 第一次掃描只建立基準：程式啟動時就已經開著的視窗不算「新的」，
    ; 所以啟動 PipDimmer 不會憑空改變任何畫面。
    if (!FirstScan && CFG["AutoApply"] && CFG["LastAlphaPercent"] < 100) {
        want := Round(CFG["LastAlphaPercent"] * 255 / 100)
        for hwnd, _ in pips
            if (!KnownPips.Has(hwnd) && !Managed.Has(hwnd))
                ApplyAlpha(hwnd, want)
    }
    FirstScan := false

    PipSet := pips
    KnownPips := pips

    ; 清掉已經關閉的視窗，避免 Map 無限長大
    for hwnd in MapKeys(Managed)
        if !WinExist("ahk_id " hwnd)
            Managed.Delete(hwnd)
    for hwnd in MapKeys(Ghosts)
        if !WinExist("ahk_id " hwnd)
            Ghosts.Delete(hwnd)

    UpdateTip()
}

MapKeys(m) {
    keys := []
    for k, _ in m
        keys.Push(k)
    return keys
}

RestoreAll() {
    for hwnd in MapKeys(Managed)
        if WinExist("ahk_id " hwnd)
            ApplyAlpha(hwnd, 255)
    Managed.Clear()
    for hwnd in MapKeys(Ghosts) {
        if WinExist("ahk_id " hwnd) {
            try {
                WinSetExStyle "-" EX_TRANSPARENT, "ahk_id " hwnd
                DllCall("SetWindowPos", "ptr", hwnd, "ptr", 0, "int", 0, "int", 0, "int", 0, "int", 0, "uint", SWP_STYLE)
            }
        }
    }
    Ghosts.Clear()
    UpdateTip()
}

; ============================================================ OSD ==========
BuildOsd() {
    global OsdGui, OsdLabel, OsdPct, OsdTrack, OsdHwnd
    ; +E0x20 = WS_EX_TRANSPARENT（滑鼠穿透，絕不擋到任何點擊）
    ; +E0x08000000 = WS_EX_NOACTIVATE（絕不搶焦點）
    OsdGui := Gui("-Caption +AlwaysOnTop +ToolWindow +E0x20 +E0x08000000 -DPIScale", "PipDimmerOsd")
    OsdGui.BackColor := "1C1C20"
    OsdGui.MarginX := 0, OsdGui.MarginY := 0

    OsdGui.SetFont("s9 c9C9CA8", "Segoe UI")
    OsdLabel := OsdGui.Add("Text", "x14 y10 w172 h16", "")
    OsdGui.SetFont("s15 bold cFFFFFF", "Segoe UI")
    OsdPct := OsdGui.Add("Text", "x12 y26 w176 h26", "")

    OsdTrack := OsdGui.Add("Progress", "x14 y56 w172 h5 Background3A3A42 c60ACFF", 0)

    OsdGui.Show("w200 h70 Hide NoActivate")
    OsdHwnd := OsdGui.Hwnd
    WinSetTransparent 240, "ahk_id " OsdHwnd
}

ShowOsd(hwnd, alpha) {
    if !CFG["ShowOsd"]
        return
    label := PipSet.Has(hwnd) ? "子母畫面" : TrimTitle(WinGetTitle("ahk_id " hwnd))
    if Ghosts.Has(hwnd)
        label .= "（穿透中）"
    pct := Round(alpha * 100 / 255)
    OsdLabel.Value := label
    OsdPct.Value := pct "%"
    OsdTrack.Visible := true
    OsdTrack.Value := pct
    PlaceOsd()
}

OsdMessage(text) {
    if !CFG["ShowOsd"] {
        TrayTip "PipDimmer", text
        return
    }
    OsdLabel.Value := ""
    OsdPct.Value := text
    OsdTrack.Value := 0
    OsdTrack.Visible := false
    PlaceOsd()
}

PlaceOsd() {
    MouseGetPos(&mx, &my)
    mon := MonitorWorkAreaAt(mx, my)
    x := mx + 22, y := my + 24
    if (x + 200 > mon.right)
        x := mx - 200 - 22
    if (y + 70 > mon.bottom)
        y := my - 70 - 24
    if (x < mon.left)
        x := mon.left
    if (y < mon.top)
        y := mon.top
    OsdGui.Show("x" x " y" y " w200 h70 NoActivate")
    SetTimer HideOsd, -850
}

HideOsd() {
    try OsdGui.Hide()
}

MonitorWorkAreaAt(x, y) {
    loop MonitorGetCount() {
        MonitorGetWorkArea(A_Index, &l, &t, &r, &b)
        if (x >= l && x < r && y >= t && y < b)
            return { left: l, top: t, right: r, bottom: b }
    }
    MonitorGetWorkArea(MonitorGetPrimary(), &l, &t, &r, &b)
    return { left: l, top: t, right: r, bottom: b }
}

TrimTitle(t) {
    if (t = "")
        return "視窗"
    return StrLen(t) > 22 ? SubStr(t, 1, 21) "…" : t
}

; =========================================================== 系統匣 ========
BuildTray() {
    tm := A_TrayMenu
    tm.Delete()

    tm.Add "啟用", MenuToggle.Bind("Enabled")
    tm.Add

    stepMenu := Menu()
    for v in [2, 5, 10]
        stepMenu.Add v "%", MenuPick.Bind("StepPercent", v)
    tm.Add "每格步進", stepMenu

    minMenu := Menu()
    for v in [10, 20, 30]
        minMenu.Add v "%", MenuPick.Bind("MinPercent", v)
    tm.Add "最低透明度", minMenu

    ; 單獨 Ctrl 有提供但不是預設：Ctrl+滾輪是瀏覽器／VS Code／檔案總管的縮放鍵，
    ; Ctrl+中鍵則是「在新分頁開啟連結」。
    modMenu := Menu()
    for pair in [[0, "無（直接滾，會吃掉所有視窗的滾輪）"]
               , [2, "Shift"], [4, "Alt"], [8, "Win"]
               , [1, "Ctrl（會蓋掉瀏覽器縮放）"]
               , [3, "Ctrl+Shift"], [5, "Ctrl+Alt"], [6, "Shift+Alt"]]
        modMenu.Add pair[2], MenuPickMod.Bind(pair[1], pair[2])
    tm.Add "修飾鍵（其他視窗）", modMenu
    global ModMenu := modMenu

    tm.Add "顯示 OSD 百分比",                 MenuToggle.Bind("ShowOsd")
    tm.Add "新的子母畫面自動套用上次透明度",  MenuToggle.Bind("AutoApply")
    tm.Add "允許 修飾鍵+滾輪 調整其他視窗",   MenuToggle.Bind("AllowOtherWindows")
    tm.Add "修飾鍵+中鍵 切換滑鼠穿透",        MenuToggle.Bind("GhostGesture")
    tm.Add "反轉滾輪方向",                    MenuToggle.Bind("InvertWheel")
    tm.Add "套用後強制重繪（畫面異常時打開）", MenuToggle.Bind("ForceRedraw")
    tm.Add

    tm.Add "還原所有視窗", (*) => (RestoreAll(), OsdMessage("已還原所有視窗"))
    tm.Add "偵測視窗資訊…", (*) => ShowWindowInfo()
    tm.Add
    tm.Add "結束", (*) => ExitApp()

    SyncTray()
    A_IconTip := "PipDimmer (AHK)"
}

SyncTray() {
    tm := A_TrayMenu
    for key, label in Map("Enabled", "啟用"
                        , "ShowOsd", "顯示 OSD 百分比"
                        , "AutoApply", "新的子母畫面自動套用上次透明度"
                        , "AllowOtherWindows", "允許 修飾鍵+滾輪 調整其他視窗"
                        , "GhostGesture", "修飾鍵+中鍵 切換滑鼠穿透"
                        , "InvertWheel", "反轉滾輪方向"
                        , "ForceRedraw", "套用後強制重繪（畫面異常時打開）") {
        if CFG[key]
            tm.Check label
        else
            tm.Uncheck label
    }
    SyncModMenu()
}

MenuToggle(key, *) {
    CFG[key] := CFG[key] ? 0 : 1
    SyncTray()
    SaveSoon()
    UpdateTip()
}

MenuPick(key, value, *) {
    CFG[key] := value
    SaveSoon()
}

MenuPickMod(mask, label, *) {
    CFG["ModifierMask"] := mask
    SyncModMenu()
    ApplyModHotkeys()
    SaveSoon()
    OsdMessage("修飾鍵：" ModName(mask))
}

SyncModMenu() {
    global ModMenu
    for pair in [[0, "無（直接滾，會吃掉所有視窗的滾輪）"]
               , [2, "Shift"], [4, "Alt"], [8, "Win"]
               , [1, "Ctrl（會蓋掉瀏覽器縮放）"]
               , [3, "Ctrl+Shift"], [5, "Ctrl+Alt"], [6, "Shift+Alt"]] {
        if (CFG["ModifierMask"] = pair[1])
            ModMenu.Check pair[2]
        else
            ModMenu.Uncheck pair[2]
    }
}

UpdateTip() {
    n := PipSet.Count
    if !CFG["Enabled"]
        s := "已停用"
    else if (n = 0)
        s := "尚未偵測到子母畫面"
    else {
        parts := []
        for hwnd, _ in PipSet
            parts.Push(Round(CurrentAlpha(hwnd) * 100 / 255) "%")
        s := "子母畫面 " n " 個：" JoinArr(parts, "、")
    }
    A_IconTip := "PipDimmer (AHK) — " s
}

JoinArr(a, sep) {
    out := ""
    for v in a
        out .= (out = "" ? "" : sep) v
    return out
}

ShowWindowInfo() {
    OsdMessage("3 秒後擷取游標下的視窗…")
    SetTimer GrabInfo, -3000
}

GrabInfo() {
    MouseGetPos(&mx, &my, &hwnd)
    if !hwnd {
        MsgBox "游標下沒有視窗。", "PipDimmer"
        return
    }
    try {
        cls  := WinGetClass("ahk_id " hwnd)
        title := WinGetTitle("ahk_id " hwnd)
        proc := WinGetProcessName("ahk_id " hwnd)
        pid  := WinGetPID("ahk_id " hwnd)
        st   := WinGetStyle("ahk_id " hwnd)
        ex   := WinGetExStyle("ahk_id " hwnd)
        WinGetPos(&x, &y, &w, &h, "ahk_id " hwnd)
    } catch as e {
        MsgBox "讀取視窗資訊失敗：" e.Message, "PipDimmer"
        return
    }

    pipDetected := IsPip(hwnd)
    msg := "游標位置    : " mx ", " my "`n"
        . "HWND        : 0x" Format("{:X}", hwnd) "`n"
        . "類別        : " cls "`n"
        . "標題        : " title "`n"
        . "程序        : " proc "  (PID " pid ")`n"
        . "style       : 0x" Format("{:08X}", st) "`n"
        . "exstyle     : 0x" Format("{:08X}", ex) "`n"
        . "位置大小    : (" x "," y ") " w "x" h "`n`n"
        . "TOPMOST     : " ((ex & EX_TOPMOST) ? "是" : "否") "`n"
        . "LAYERED     : " ((ex & EX_LAYERED) ? "是" : "否") "`n"
        . "TRANSPARENT : " ((ex & EX_TRANSPARENT) ? "是（滑鼠穿透）" : "否") "`n"
        . "目前透明度  : " Round(CurrentAlpha(hwnd) * 100 / 255) "%`n`n"
        . "判定為子母畫面：" (pipDetected ? "是（滾輪可直接調整）" : "否（修飾鍵：" ModName(CFG["ModifierMask"]) "）")

    if !pipDetected {
        msg .= "`n`n未判定為子母畫面的可能原因："
        if (cls != "Chrome_WidgetWin_1")
            msg .= "`n  · 視窗類別不是 Chrome_WidgetWin_1"
        if (ex & EX_NOACTIVATE)
            msg .= "`n  · 視窗是不可啟動的覆蓋層（WS_EX_NOACTIVATE）"
        if !(ex & EX_TOPMOST)
            msg .= "`n  · 視窗沒有置頂，且標題也不符合已知的子母畫面標題"
        if (CFG["RequirePipShape"] && (st & WS_MINIMIZEBOX) && (st & WS_MAXIMIZEBOX))
            msg .= "`n  · 同時有最小化與最大化鈕，形狀像一般瀏覽器視窗。"
                .  "若這確實是子母畫面，請在 settings.ini 設 RequirePipShape=0"
    }
    MsgBox msg, "PipDimmer — 視窗資訊"
}

; ========================================================== 設定存取 =======
; 手工解析／寫出，格式跟 C# 版逐字一致（純 key=value，沒有 [Section]），
; 這樣兩個版本可以共用同一份 settings.ini。
LoadCfg() {
    if !FileExist(CfgPath)
        return
    try content := FileRead(CfgPath, "UTF-8")
    catch
        return
    for line in StrSplit(content, "`n", "`r") {
        line := Trim(line)
        if (line = "" || SubStr(line, 1, 1) = "#" || SubStr(line, 1, 1) = ";")
            continue
        p := InStr(line, "=")
        if !p
            continue
        k := Trim(SubStr(line, 1, p - 1))
        v := Trim(SubStr(line, p + 1))
        if (CFG.Has(k) && IsInteger(v))
            CFG[k] := Integer(v)
    }
    Clamp()
}

Clamp() {
    if !(CFG["StepPercent"] = 2 || CFG["StepPercent"] = 5 || CFG["StepPercent"] = 10)
        CFG["StepPercent"] := 5
    if !(CFG["MinPercent"] = 10 || CFG["MinPercent"] = 20 || CFG["MinPercent"] = 30)
        CFG["MinPercent"] := 10
    if (CFG["LastAlphaPercent"] < CFG["MinPercent"])
        CFG["LastAlphaPercent"] := CFG["MinPercent"]
    if (CFG["LastAlphaPercent"] > 100)
        CFG["LastAlphaPercent"] := 100
    if (CFG["ModifierMask"] < 0 || CFG["ModifierMask"] > 15)
        CFG["ModifierMask"] := 3
}

SaveSoon() {
    SetTimer SaveCfg, -1500
}

SaveCfg() {
    Clamp()
    dir := A_AppData "\PipDimmer"
    if !DirExist(dir)
        try DirCreate dir
    out := "# PipDimmer settings`n"
    for k in ["Enabled", "StepPercent", "MinPercent", "LastAlphaPercent", "ShowOsd"
            , "AutoApply", "AllowOtherWindows", "GhostGesture", "InvertWheel", "ForceRedraw"]
        out .= k "=" CFG[k] "`n"
    ; 逐字比照 C# 版的輸出格式，兩邊才能共用同一份 settings.ini
    out .= "# Modifier bitmask for acting on non-PiP windows:`n"
         . "#   1=Ctrl  2=Shift  4=Alt  8=Win   (add them together, 0 = none)`n"
         . "ModifierMask=" CFG["ModifierMask"] "`n"
         . "# Set to 0 only if a PiP window stops being detected because it`n"
         . "# has both a minimize and a maximize box.`n"
         . "RequirePipShape=" CFG["RequirePipShape"] "`n"
    try {
        if FileExist(CfgPath)
            FileDelete CfgPath
        FileAppend out, CfgPath, "UTF-8-RAW"
    }
}

Cleanup(*) {
    ; 結束時把碰過的視窗全部還原，不要留下半透明或點不到的視窗
    try RestoreAll()
    try SaveCfg()
}
