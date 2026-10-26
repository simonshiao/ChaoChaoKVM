# LAN 低延遲修正 · 2026-09-14

正式目標：`src/ChaoChaoKvm.Net472` 與根目錄 `ChaoChaoKvm.exe`。保留 HTTP 8001、既有 IP 配對、Agent API、UI 與傳檔格式。

## 已確認的來源

- 本機既有設定為 **5 FPS / JPEG 55**，每次擷取間隔約 200ms。此輪將本機改為 **20 FPS / JPEG 55**；這是本機設定，另一台仍須確認自己的 FPS。
- 原本單一 PollLoop 依序處理心跳、frame、反向 input、完整檔案下載，再固定 Sleep(35)。慢檔案下載會暫停同一側的 frame / input polling。
- 反向輸入每輪只拉一筆。32 個事件約需 1.5 秒才能清空。
- 每次合法 peer 請求都會進入 OnPeerSeen → SaveConfig，重複寫磁碟；每個 frame 又重複發布沒有改變的解析度資訊。
- 擷取後同步 PushFrame，網路慢時下一次擷取也被卡住。每張 frame 都重新建立 Bitmap / Graphics 與查詢 JPEG codec。
- 原生畫面另外每 50ms 才嘗試顯示；本機 move 事件限制為 28ms 一筆。

## 實作

1. frame、input、bulk transfer 三個獨立 worker。輪詢以取消 token 管理，每次重新連線取消舊 worker 並中止在途 request，避免 Stop/Start 共用 `_running` 讓舊 thread 復活。frame 序號各 worker 獨立。
2. frame 依原有 FPS 節奏輪詢；反向 input 空閒每 8ms 拉取，支援 `?batch=1` 每次最多 64 筆。新 client 仍接受舊 server 的單筆 payload；舊 client 的無 query endpoint 完全保留。
3. 只合併相鄰 move，保留 key/button 及 release 的次序，移除舊版滿載時 Clear 全佇列的行為。4096 筆上限以錯誤拒絕新增，沒有靜默刪除既有事件；長時間斷線仍無法保證遠端收得到 release。
4. 僅在對端 ServicePoint 設定 `Expect100Continue=false`、`UseNagleAlgorithm=false`、`ConnectionLimit>=16`；Server socket 使用 NoDelay，並支援舊 client 的 `100 Continue`。保留 `Connection: close`，未直接切換 persistent HTTP。
5. Capture 與 Push 分工，待送 frame 僅保留最新一張，避免網路塞住下一次擷取。重用同解析度 Bitmap / Graphics，正確釋放 EncoderParameters，結束擷取時 dispose 表面。
6. 配對資訊或解析度實際改變才保存／通知。原生 render 排程 50ms → 16ms，move 節流 28ms → 12ms。

HTTP 行為參考：[Microsoft ServicePoint.Expect100Continue](https://learn.microsoft.com/en-us/dotnet/api/system.net.servicepoint.expect100continue?view=netframework-4.8.1)。修改限於現有桌面 Framework 架構，沒有引入新通訊套件。

## 修改前後量測

相同電腦、localhost、20 FPS 合成 128KiB frame、32 筆合成鍵鼠事件；測試 handler 只計數，**未注入 OS 鍵鼠、未擷取實際桌面**。慢傳檔案例在 1MiB 測試檔傳送途中暫停 1500ms，檔案接收後逐 byte 核對。每次測試使用獨立目錄，避免舊檔造成假成功。

| 測量 | 修改前 | 修改後 |
|---|---:|---:|
| 初次判定連線成功 | 420ms | 42ms |
| 反向 32 筆 input 完整送達 | 1489ms | 21ms |
| 慢傳檔期間的反向 input 等待 | 1574ms | 26ms |
| 直接 input request p95 | 0.65ms | 0.61ms |
| 合成 frame 接收年齡 p95 | 47.62ms | 1.03ms |

直接 input 的 localhost 往返原本已很快；主要收益是反向佇列、bulk download 阻塞与排程。Frame 年齡受 publisher/polling 相位影響，不是桌面完整延遲。**不能把上述数字當成兩台實機的 LAN、JPEG encode/decode、WPF 顯示和螢幕刷新總延遲保證。**

## 驗證與重跑

- Release 建置。
- QueueTests：大量 move 不丟失 key/button release，batch 與 legacy 單筆 payload，舊 client 的 `100 Continue`。
- LatencyBench：連線、直接／反向 input、慢傳檔時仍能收 frame/input、檔案逐 byte 一致、停止後無新 frame、快速重連八次。回環門檻為 burst <600ms、bulk input <500ms，留有排程餘裕。
- CompatibilityProbe：新 client / 舊 server，及舊 client / 新 server；兩者 frame、反向 input、直接 input 都通過。
- 既有 UiBehaviorTests 和 Web 狀態測試作回歸。

`tests/RunLatencyChecks.ps1 -BaselineExe <本輪備份的舊ChaoChaoKvm.exe>` 可重跑。測試只用合成資料；舊 server 的既有 Start 方法綁定 Any，新 server 測試則限制 Loopback。

尚未做兩台實機的長時間 LAN / 高解析度桌面效能驗收。實機使用時建議兩端都更新完整程式目錄並設 20 FPS；保留現有 55 清晰度先比較手感，之後再視 CPU／網路負擔調整。這版仍為完整 JPEG frame 傳輸，不是硬體 H.264／差分編碼。
