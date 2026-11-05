# ChaoChaoKvm · Computer Use 遠端手腳（2026-11-05）

## 定位

不另建 Agent、MCP Gateway 或 PowerShell 終端。Agent / Codex 延續既有 @CHAO2 CHAO MCP Hub；KVM 僅提供遠端畫面、鍵鼠。原有 HTTP 8001、JSON Input Queue／反向輪詢與遠端允許控制設定不變，且不新開網路埠。

`tools/kvm_computer.py` 使用既有 loopback 127.0.0.1:45889 LocalAgentApi；Agent 可透過 @CHAO2 的 `exec_command` 呼叫。

## 使用

```powershell
python -B tools/kvm_computer.py status
python -B tools/kvm_computer.py screenshot --out "E:\0_軟體開發區\4.測試實驗區\KVM\remote.jpg"
python -B tools/kvm_computer.py click --x 450 --y 320
python -B tools/kvm_computer.py hotkey --keys ctrl a
python -B tools/kvm_computer.py type --text "測試文字"
python -B tools/kvm_computer.py key --key enter
python -B tools/kvm_computer.py scroll --x 700 --y 440 --delta -120
```

`--peer 192.168.x.x` 可指定已連線電腦；省略時採用目前 ActivePeerIp。座標是遠端 JPEG 原始像素，不是縮放後 KVM 視窗座標。

## 本機 API

一行 UTF-8 JSON 請求、一行 JSON 回覆。新增命令：

| cmd | 參數 |
| --- | --- |
| `computer_status` | `peer` 可省略 |
| `computer_screenshot` | `peer` 可省略；回傳 JPEG Base64、寬高與時間 |
| `computer_click` | `x`, `y`, `button`, `clicks` |
| `computer_move` | `x`, `y` |
| `computer_scroll` | `x`, `y`, `delta` |
| `computer_key` | `key` |
| `computer_hotkey` | `keys` 字串陣列 |
| `computer_type` | `text`，最多 2048 UTF-16 code units |

**限制：** Unicode 輸入需遠端 KVM 同步新版；舊版僅能接受既有按鍵。事件回傳 `queued=true` 不保證遠端已處理，應再次截圖確認。超過 5 秒的舊影格會拒絕輸出。此版本沒有遠端 shell／檔案寫入新能力，也不要把 loopback API 公開轉接到外網。

## 驗收

隔離建置、39 項 UI 行為與 LAN 探索回歸；正式版重啟後再由 CLI 實測 `help/status/screenshot`。滑鼠和鍵盤需從新的遠端畫面確認，輸入焦點可能被其他應用程式切換。遠端 Unicode 驗收需兩側同步更新。
