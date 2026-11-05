# ChaoChaoKvm — Agent / Codex 專案入口

> 此檔是精簡的 Agent 工作入口。所有詳細指令與 API 參數以 `docs/COMPUTER_USE.md` 為準；交接／部署狀態以 `HANDOFF.md` 為準。

## 這個專案的定位

- **ChaoChaoKvm 只做遠端畫面、滑鼠與鍵盤**；CHAO MCP Hub 是既有 Agent／MCP 入口。不要重建 KVM 專用 Agent、MCP Server、PowerShell 終端或另一份 Runtime。
- 若任務是「透過 KVM 操作遠端電腦」，先讀 `docs/COMPUTER_USE.md`。若任務是修改 KVM 原始碼，先看 `README.md` 的來源與建置入口，再讀 `HANDOFF.md` 的最新段落。
- 這個專案的正式來源是 `src/ChaoChaoKvm.Net472`；正式發行為 `Release/Recovery`；不要把根目錄的舊 `Web` 當正式 Recovery UI。
- **可攜版入口**：`Release/Recovery` 也會隨建置附上本檔、`docs/COMPUTER_USE.md` 與 `tools/kvm_computer.py`，讓複製整個 Release 資料夾的 Agent 能找到操作方式；僅有 Release 不等於包含原始碼，修改／重建請回正式來源專案。
- **Cloudflare／跨網路**：先讀 `docs/CLOUDFLARE_RECOVERY.md`；可用 `powershell -NoProfile -File tools/check_cloudflare.ps1` 做不洩露 Token 的預檢。Cloudflare Access 登入頁正常不等於 KVM Relay 已完成 WebSocket 握手。不要把 CHAO MCP 的 Cloudflare 網址當 KVM 的服務端點。
- **免 Google 登入**：KVM 新增 Cloudflare Service Token 模式，於 KVM「網路連線」頁設定，由 DPAPI 保存在各主機使用者資料；**不得**向使用者索取 Token 放進對話／Git／Log。Cloudflare Zero Trust 必須有指定 Service Token 的 Service Auth 政策，詳見 `docs/CLOUDFLARE_RECOVERY.md`。

## Agent 使用 Computer Use 的最短路徑

在 **@CHAO2** 上、工作目錄為本專案根目錄時：

```powershell
python -B tools/kvm_computer.py status
python -B tools/kvm_computer.py screenshot --out "E:\0_軟體開發區\4.測試實驗區\KVM\remote.jpg"
```

根據**最新遠端原始解析度截圖**，才執行 `click --x ... --y ...`、`move`、`scroll`、`key`、`hotkey` 或 `type`。必要時加 `--peer <已連線的內網IP>`，不指定時使用 KVM 的 ActivePeerIp。

**每次重要輸入後重新截圖驗證。** `queued=true` 只表示事件已送達／排隊，不代表遠端按鈕已點到。遠端鎖定、畫面過時、裝置離線時不要盲目輸入；Unicode 文字輸入必須雙端都有支援的新版 KVM。

這條通道**不提供遠端 shell、任意指令或無驗證的網路 API**。既有 Agent 若能透過目標電腦的 MCP 讀檔與執行程式，仍優先用既有 MCP 工具。

## 開發與驗證

- 最小改動，不改動 CHAO MCP Hub 的工具契約或建立重複服務。
- 先以 `tests/RunUiChecks.ps1 -OutputDirectory <測試資料夾>` 在隔離輸出編譯；如涉及連線探索，再以 `tests/RunLanDiscoveryChecks.ps1 -BuildDirectory <同一測試資料夾>` 驗證。
- 正式部署使用根目錄 `建置.ps1` 與 `啟動.vbs`；正式執行中要先正常關閉指定的 KVM 程序，不要硬覆寫鎖定的 EXE 或關閉別的程序。
- 操作結果與待驗證項寫入 `HANDOFF.md`；不可把單機測試誤報為公司端鍵鼠實際互通。
