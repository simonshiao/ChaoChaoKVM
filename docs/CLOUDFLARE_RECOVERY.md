# ChaoChao KVM Recovery：Cloudflare 遠端連線

Recovery Cloudflare 版保留 LAN 版 HTTP 8001。Cloudflare 遠端連線與 LAN 互相獨立，不應開放公網 TCP 8001，更不應將 CHAO MCP 的 `mcp.chaochao.win` 當成 KVM 網域。

## 2026-11-05 現行版：先驗證 CCF2 HTTPS 中繼

- **無 Google／中國大陸環境的建議驗證方式（Service Token）**：在可登入 Cloudflare 控制台的電腦，進入 Zero Trust → Access controls → Service credentials → Service Tokens，建立 HOME-1 與 TW 各自的 Token；到 `kvm-relay.chaochao.win` 所在的 **Self-hosted Access Application** 設定 `Service Auth` 政策，僅 Include 這兩個 Token。勿刪除其他必要的 Allow 政策，也不可設定 Bypass。
- 在兩台新版 KVM 的 **網路連線 → Cloudflare 免 Google 登入設定** 分別輸入自己的 Client ID 與 Client Secret，按「儲存免登入憑證」。程式會使用 Windows DPAPI CurrentUser 加密儲存；UI 只回傳是否設定，不回傳憑證內容。之後 CCF2 Host／Client WebSocket 直接送 `CF-Access-Client-Id` 與 `CF-Access-Client-Secret`，不執行 `cloudflared access login`，也不嘗試 Google 登入。
- 尚未設定 Service Token 的電腦維持原有 Access 瀏覽器登入模式。若 Cloudflare 政策未設定或憑證失效，應回報驗證錯誤，**不能自動降級成匿名連線／公開 Bypass**。Credential 並非 KVM 房間配對金鑰；CCF2 自身房間金鑰仍必須保留。
- 原 Client Secret 僅在 Cloudflare 建立 Token 時顯示一次，請安全保存並勿貼給 Agent 聊天；可在 Cloudflare 控制台獨立停用或輪替憑證。Token 逾期後需更換。驗收必須實際得到 WebSocket HTTP 101 與遠端畫面；有了 Service Token 仍不能保證跨境網路可靠。
- **目前 UI 的「網路連線／啟用遠端連線」是 CCF2 Relay 路線**：被控制端向 `wss://kvm-relay.chaochao.win/relay` 以 host 角色連線；控制端貼上 CCF2 連線碼後，建立 HTTPS／WebSocket 中繼，仍使用 KVM 既有畫面、鍵鼠、檔案協定。
- 中繼必須有支援 CCF2 `OPEN/OPENED/FAILED`、房間配對及二進位串流的服務端，並以 **Cloudflare Access 的 hostname-based policy** 保護 WebSocket；只有登入頁回傳 HTTP 200 **無法證明** Relay Worker／Durable Object 或 WebSocket 101 握手成功。
- 目前兩台 LAN 已驗證正常；HOME-1 與 TW 都能看到 `kvm-relay.chaochao.win` 的 Access 登入頁，但 CCF2 **尚未完成跨網路串流實測**，不能標示為已驗收。
- 首次使用需登入 Cloudflare Access。請先確認 relay 主機服務端與 Zero Trust 允許規則，不要停用身分驗證，亦不在對話、Git 或日誌分享 CCF2 連線碼（包含配對金鑰）。
- 診斷：在 KVM 專案根目錄或完整 Release 執行 `powershell -NoProfile -File tools/check_cloudflare.ps1`。僅列連線狀態及是否有受保護設定，不輸出 Token、Room ID、配對金鑰。
- 正式 `建置.ps1` 會嘗試從開發機已安裝、簽章有效的 Cloudflare 官方 `cloudflared.exe` 一併封裝到 `Release/Recovery`；獨立拷貝新版 Release 時不需再另找它。若建置機未安裝會顯示警告，此包的雲端連線仍需安裝工具。
- Cloudflare 的全球網路由中國大陸連線仍可能有跨境網路的延遲／不穩；請**在實際中國大陸網路**驗收，不以兩台都在台灣同 LAN 的測試替代。正式操作採 LAN 優先、Cloudflare 作跨網路備援。

官方參考：
- https://developers.cloudflare.com/network/websockets/
- https://developers.cloudflare.com/workers/configuration/cloudflare-access/ （Worker-level Access 不支援 WebSocket；須改用 hostname-based Access 應用程式）
- https://developers.cloudflare.com/china-network/

## 相容模式：CCF1 Named Tunnel（既有備援）

1. 在 Cloudflare Zero Trust 的 **Networks → Tunnels** 建立一條 Named Tunnel，選 Windows 並複製 Tunnel Token。
2. 在該 Tunnel 新增 TCP Published application route：主機名稱使用 `kvm.chaochao.win`（或 `chaochao.win` 底下尚未使用的子網域），Service 設為 `tcp://localhost:8001`。
3. 在 **Access → Applications** 為該主機名稱建立 Access 應用程式與允許使用者的登入政策。建議只允許自己的登入帳號。未設定 Access Policy 時，不要啟動公開入口。
4. 在每台要發起遠端連線的 Windows 電腦安裝 Cloudflare `cloudflared`。程式會自動尋找 PATH、`C:\Program Files (x86)\cloudflared\cloudflared.exe` 或程式資料夾內的 `cloudflared.exe`。

## 被控制端啟動入口

1. 開啟 Recovery，進入 **Cloudflare 網路**。
2. 輸入 Tunnel route 的公開主機名稱與 Tunnel Token，按「保存設定」，再按「啟動 Tunnel」。
3. 狀態顯示「Tunnel 已連線」後，複製本機連線碼給控制端。連線碼不含 Tunnel Token。

Tunnel Token 以 Windows DPAPI 保護後儲存在 `%LOCALAPPDATA%\ChaoChaoKvm\cloudflare.json`。Tunnel 啟動時會短暫寫入使用者暫存目錄，cloudflared 讀取後即刪除。

## 控制端連線

1. 開啟 Recovery，進入 **Cloudflare 網路**，貼上對方的 `CCF1...` 連線碼。
2. 按「開啟遠端桌面」。`cloudflared access tcp` 會建立本機轉接並在需要時開啟瀏覽器，完成 Cloudflare Access 登入後即可連線。
3. 「停止遠端轉接」會結束此程式啟動的所有 Access TCP 用戶端程序。

LAN 版「遠端連線」仍使用原本 IPv4、HTTP 8001 與既有裝置資料；Cloudflare 連線獨立透過本機轉接建立遠端桌面 Session。
