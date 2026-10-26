# 吵吵 KVM：淺色桌面工作空間

## 2026-10-16 遠端畫面改版（最新正式版）

目前正式遠端操作畫面為 `src/ChaoChaoKvm.Net472/MultiSessionWindow.xaml`；此節取代下方 9 月版的浮動玻璃工具列敘述。首頁 WebView2 的淺色設計與底層 `CHAO_KVM_HTTP_V2` 均保留，沒有更換傳輸核心。

- 遠端視窗頂部改為固定 **56px 單列工具列**，真正的遠端影像在工具列下方 Grid.Row=1，沒有漂浮於遠端影像上的按鈕、分頁或底部提示。
- 左側「返回」回到主畫面；旁邊顯示目前遠端名稱、連線狀態圓點與 IP；中間有可捲動多遠端分頁與新增裝置；右側常用操作依序為「重連、檔案、設定、全螢幕、控制中／唯讀中、關閉分頁」。
- 「重連」呼叫既有 `RemotePeerSession.Retry()`，目前配對端也會觸發 `KvmPeerManager.RetryNow()`。仍遵循公司端 outbound／家端 inbound 的既有反向連線拓樸。
- 「控制中／唯讀中」只影響**本機是否傳送鍵鼠**，不改遠端電腦的授權設定。點唯讀時先送出按鍵／滑鼠釋放，阻止後續鍵鼠事件；`Ctrl + Alt + Esc` 仍可暫停，按工具列可恢復。狀態寫在工具列，不再覆蓋畫面。
- 「關閉分頁」只移除目前遠端工作階段；最後一個分頁關閉會回到主畫面；「返回」關閉遠端檢視視窗；真正退出 KVM 程式需關閉主畫面，屆時才釋放 8001／45889。
- 小視窗優先保留所有常用按鈕及電腦身份，分頁區可水平捲動；全螢幕仍保留同一條工具列，不再依賴滑鼠滑入玻璃動畫才能看清按鈕。

已驗證：Release 建置、`RunUiChecks.ps1` 39 項 UI 行為測試、Web 狀態測試；獨立未連線視窗預覽可啟動並已正常關閉。HOME-1 與 TW 都已切換至新 EXE（共用 SHA-256：`28046EA40545FEE520965D3E6B0222730009E45CC0921A5D4D351C6F9161BA29`），新舊版並未混用；上版 EXE 備份於 `docs/release-backup-20261016-toolbar/ChaoChaoKvm.exe`。

未驗證：工具列在真實遠端桌面中連續操作的主觀手感，以及實際鍵鼠／檔案傳輸全功能 E2E；現有測試不宣稱通過這些項目。單工作階段舊版 `SessionWindow.xaml` 留作相容來源與回歸測試，正式進入遠端工作區仍走 `MultiSessionWindow`。

---

2026-09-14。正式程式為根目錄 `ChaoChaoKvm.exe`，使用 `src/ChaoChaoKvm.Net472`。原有 `src/ChaoChaoKvm.App` 的 .NET 8 版本並非本次修改目標。

## 設計方向

Apple 式系統字體、留白、穩定層級、按壓回饋；以 Web UI 作連線與設定工作空間，原生 WPF 顯示遠端 JPEG 桌面。遠端工作階段採半透明玻璃懸浮層：左上顯示裝置狀態、右上為圖示工具列、底部為輕量操作提示，遠端畫面本身鋪滿整個視窗。這是介面重整，保留目前 HTTP 8001 的雙向連線、傳檔協定、設定格式與 Agent API。

2026-09-15 新增「裝置中心 / 多工作階段」：可用群組、標籤、收藏管理多台電腦；每台遠端工作階段在同一個原生視窗中形成分頁。HTTP 8001 Host 依來源 IP 分流反向連線、鍵鼠 queue 與回拉傳輸，讓公司端 inbound 被擋時仍可同時管理多台設備。

- 使用者：在兩台 Windows 電腦之間操作、傳檔的桌面使用者。
- 模式：Redesign / Overhaul；只重整介面與使用者輸入路徑。
- 五個設計尺度：視覺變化 3、動態 2、資訊密度 4、素材依賴 1、原品牌保留 6。
- 定位：日常工作入口；筆電／桌面近距閱讀；安靜且可信賴；主畫面放兩個核心區塊，不堆放診斷資訊。
- 色彩：霧白 `#F5F7FA`、白色 `#FFFFFF`、文字 `#202936`、次文字 `#697586`、主操作藍 `#1768C5`。綠／琥珀僅表示連線狀態。
- 字體：系統字體，Windows 使用 Segoe UI 與 Microsoft JhengHei UI；大標題 27–38px，控制項 12–15px。
- 間距：以 4px／8px 倍數為主；桌面頁面留白 42px。面板圓角 20px、輸入 12px、按鈕 8–9px。
- 動態：160ms 顏色與小幅按壓回饋；支援 reduced-motion 與高對比偏好。沒有持續動畫、假資料、假裝置、假連線數字。
- 工作階段玻璃層採「滑入浮現」：游標進入視窗頂部區域時，左上裝置資訊與右上操作台在約 170ms 內提高不透明度；離開後自動淡化，減少遮擋遠端桌面。
- 工具列收合時，左上電腦名稱與連線狀態同步隱藏，只保留右上單一展開控制；重新展開時兩者一起恢復。收合與淡化均不改變直接操作模式。
- 不加入外部字體、CDN、圖片與追蹤服務。

參考操作模式：
- [AnyDesk Session settings](https://support.anydesk.com/session-settings)：連線期間集中於工作階段工具列。
- [TeamViewer Remote control](https://www.teamviewer.com/en/global/support/knowledge-base/teamviewer-remote/remote-control/)：遠端操作與傳檔分工。

這些是流程參考，未複製品牌 Logo，也不代表達到其完整功能或安全能力。

## 操作流程

1. 開啟程式，保留既有自動連線行為。首頁显示本機 IP、允許對方控制的狀態，以及已記住的遠端位址。
2. 輸入有效區域網路 IPv4，按「連線」。手動連線成功後開啟遠端桌面；自動背景連線不搶走首頁焦點。
3. 遠端桌面畫面可用後直接進入操作模式，不再需要「開始控制」按鈕；滑鼠移入即可控制，點一下遠端畫面即可把鍵盤焦點交給遠端。頂部玻璃層平時淡化，游標靠近頂部時才清楚浮現。
4. Ctrl + Alt + Esc 會釋放已按下的遠端按鍵／滑鼠並暫停輸入；再次點一下遠端畫面即可繼續。視窗失焦或開啟工具時也會先送出釋放事件。底層連線已斷時無法保證釋放能抵達遠端。
5. 右上玻璃工具列以圖示提供「傳檔」「設定」「全螢幕」「關閉檢視」，最左側箭頭可將整組工具列收合成單一膠囊；收合時左上裝置資訊也一起隱藏，不影響直接操作模式。
6. 「傳檔」「設定」回到 Web 工作空間；「關閉檢視」只關閉觀看視窗，明確保留本機背景連線服務。真正結束程式請關閉主視窗。
7. 「裝置中心」保存名稱、IP、群組、標籤、收藏與最後連線資訊至 `%LOCALAPPDATA%/ChaoChaoKvm/devices.json`；裝置可直接開成遠端分頁，不必重複輸入 IP。
8. 多個遠端分頁同時存在時，只有目前分頁解碼／渲染畫面。直連背景 Session 會降到 2 FPS；反向回連仍由遠端端點決定推送頻率，但背景分頁不做 JPEG 解碼與 WPF 更新，因此不把 2 FPS 誤寫成所有傳輸模式的保證。

未加入不存在的裝置 ID、密碼配對、雲端中繼、遠端權限回報、剪貼簿同步或多螢幕切換。設定中的 FPS／清晰度明確標示影響「本機送出的畫面」。

## 技術與修正

- WebView2 載入本機 `Web/` 靜態介面，使用固定虛擬來源，封鎖外部導頁與新視窗，bridge 核對訊息來源。
- 原生遠端視窗從實際 JPEG 尺寸計算座標，保留等比顯示；忽略留白區點擊，拖曳使用滑鼠捕捉與邊界夾制。
- HTTP 輸入走獨立序列執行緒，不阻塞 UI。只合併相鄰滑鼠 move，保留按鍵／按鈕順序。切換遠端後不向新對端傳送舊視窗待送事件。
- HTTP Server 支援多個已信任 LAN peer；反向 `push/frame` 依來源 IP 發出 peer-scoped 事件，`pull/input` 使用每台獨立 queue。檔案／JSON 回拉 queue 亦記錄 target peer，避免兩台同時在線時拿錯另一台的項目。
- `RemotePeerSession` 採 hybrid transport：若該 IP 已透過反向 HTTP 活著，就直接使用共用 Host；否則才啟動 frame-only outbound client。遠端輸入依 Session IP 路由，不再依賴單一 global active peer。
- 遠端畫面僅保留最新一張待顯示 JPEG，避免 Dispatcher 積壓；停止更新四秒時隱藏舊畫面並退出控制。
- 診斷預設收合，只保留最近 100 筆紀錄。
- WebView2 profile 儲存在 `%LOCALAPPDATA%/ChaoChaoKvm/WebView2`。

## 建置與驗證

依原本 .NET Framework 的 MSBuild 路徑建置。額外使用已安裝 NuGet cache 的 `Microsoft.Web.WebView2 1.0.3537.50` 的 net462 組件；可透過 MSBuild `WebView2Package` 指定其他套件根目錄。接收端需安裝 Microsoft Edge WebView2 Runtime。必須複製完整執行檔目錄，包括兩個 WebView2 managed DLL、Web/、runtimes/，不再只複製單一 EXE。原生 loader 路徑依套件 Common.targets 使用 `runtimes/win-{x64,x86,arm64}/native`。

已執行：
- Release 建置成功。
- `UiBehaviorTests.cs`：涵蓋 WebView2 loader、等比／留白座標、輸入 queue、玻璃工具列、MultiSessionWindow、裝置資料持久化，以及 multi-peer trust / liveness / peer-specific input routing。實機另以現有 `TWCYNNB1302` 連線確認多分頁視窗可收到真實遠端畫面。
- `web-state-tests.cjs`：空狀態、連線、忙碌、錯誤、IPv4 驗證、導覽、權限設定、斷線與文字安全顯示通過。這是 VM + DOM adapter 的事件測試，不是瀏覽器測試。

未驗證：瀏覽器實際視覺／響應式畫面，因宿主 Browser URL policy 拒絕本機 HTML 預覽；未繞過此限制。亦未執行兩台實機的長時間連線、控制手感、斷線後遠端按鍵釋放、傳檔與 WebView2 正式啟動驗收。既有雙向通訊成果不能當作新 UI 的實機驗收。

可直接在瀏覽器開啟 `Web/index.html` 檢視介面預覽；會明確顯示預覽提示，沒有模擬成功連線。`ChaoChaoKvm.exe --ui-preview` 可載入 WebView2 shell 而不啟動網路服務。

回復：修改前檔案保存在 `docs/ui-backup-20260914-*`。依備份 manifest 將舊 MainWindow、csproj、啟動 bat 及舊根目錄執行檔還原；新增加但舊 csproj 未引用的 SessionWindow/Web 檔案可以保留。
