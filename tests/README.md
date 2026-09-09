# tests

```powershell
.\tests\Run-Tests.ps1              # both editions
.\tests\Run-Tests.ps1 -Only cs     # PipDimmer.exe only
.\tests\Run-Tests.ps1 -Only ahk    # PipDimmer.ahk only
```

需要 Google Chrome。`-Only ahk` 另需 AutoHotkey v2；沒裝就會自動跳過那一輪。
先跑過 `.\build.ps1`（測試會用 repo 根目錄的 `PipDimmer.exe`）。

---

## 為什麼需要一個 fixture

透明度這種東西不能靠「看起來對不對」驗證，而子母畫面視窗又不能用程式直接開——
`requestPictureInPicture()` 需要真實的使用者手勢。

`pip_fixture.html` 解掉這個問題：用 `canvas.captureStream()` 餵給一個 `<video>`，
就得到一個真正在播放的影片元素，**不需要影片檔、不需要網路**。
`PipFixture.ps1` 用獨立的 `--user-data-dir` 在固定座標開一個 Chrome `--app` 視窗，
再用 `SendInput` 送一次真實的滑鼠點擊觸發 PiP。

這樣產生的子母畫面視窗跟平常看 YouTube 開出來的在結構上完全相同：

```
style=0x16CC0000  ex=0x00200108  class=Chrome_WidgetWin_1  title=[子母畫面]
```

**安全性**：`Stop-PipFixture` 只會結束「命令列含有那個臨時設定檔路徑」的行程
（透過 CIM 讀 `CommandLine`；PS 5.1 的 `Get-Process` 沒有這個屬性）。
單純用映像名稱比對會把你正在用的 Chrome 一起殺掉。

---

## 測試項目

| 項目 | 驗證什麼 |
|---|---|
| 子母畫面上 6 格滾輪 = 6 步 | 步進運算正確，而且**沒有掉格**。AHK 版早期版本因為 `#MaxThreadsPerHotkey` 預設值，每 6 格只吃 1 格 |
| 滾輪往上提高不透明度 | 方向正確 |
| 卡在 10% 下限 | alpha 不會歸零——歸零的視窗會**點不到也抓不回來** |
| 回到 100% 清掉 `WS_EX_LAYERED` | 視窗完全回到原生繪製路徑，exstyle 精確回到 `0x00200108` |
| `Ctrl`+滾輪**沒有**被攔截 | 瀏覽器縮放沒被蓋掉 |
| `Ctrl`+滾輪**確實傳給程式** | 讀 PipDimmer 自己的鉤子計數器（`-log`）。透明度沒變只能證明「沒有動這個視窗」，證明不了事件有傳下去 |
| `Ctrl+Shift`+滾輪調暗 5 步 | 修飾鍵有生效 |
| `Ctrl+Shift`+中鍵設定 `WS_EX_TRANSPARENT` | 滑鼠穿透手勢 |
| 穿透中的視窗滾輪仍調得動 | `WindowFromPoint` 會跳過穿透視窗；這項證明「用矩形找回目標」那條後路有效，否則開了穿透就再也關不掉 |
| 乾淨結束還原所有視窗 | 兩個版本都是收到 `WM_CLOSE` 後走跟系統匣「結束」相同的還原流程 |

---

## 寫測試時踩過的坑

**用自己建的視窗，不要用使用者的視窗。**
早期版本用 `WindowFromPoint` 抓游標下的任何視窗當目標，結果測試打到了正在開著的
Obsidian 跟 PowerPoint。現在目標是這支腳本自己建的 Form，而且會**先確認游標確實落在它上面**
（`Park()` 回傳 false 就直接中止），不會給出誤導的結果。

**置頂還不夠。**
PowerPoint 放映、播放器這類同樣置頂的視窗會蓋在探測視窗上面。`Raise()` 每次量測前都會重新
`SetForegroundWindow` + `BringWindowToTop`。

**要有對照組。**
曾經出現「`Ctrl`+滾輪不會捲動 ListBox」被誤判成 PipDimmer 的 bug，
實際上 ListBox 本來就不理 `Ctrl`+滾輪。加一組「完全不啟動 PipDimmer」的對照就分辨得出來。

**`sizeof(INPUT)` 在 x64 是 40。**
`MOUSEINPUT` 是 `INPUT` union 裡最大的成員，不要額外補 padding 欄位——補了 `cbSize` 就對不上，
`SendInput` 會直接回 0 什麼都不做。
