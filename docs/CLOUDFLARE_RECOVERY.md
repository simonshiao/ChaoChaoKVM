# ChaoChao KVM Recovery：Cloudflare 遠端連線

Recovery Cloudflare 版保留 LAN 版 HTTP 8001；Cloudflare Tunnel 另加一個遠端入口。關閉 Tunnel 不會停掉 LAN 服務。

## 一次性 Cloudflare 設定

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
