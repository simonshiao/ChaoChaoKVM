# ChaoChao KVM 遠端連線

> **歷史文件**：以下 `LanKVM` / TCP `45888` 為舊架構描述，不適用於目前 `ChaoChaoKvm.exe`（.NET Framework 4.7.2）。目前正式傳輸為雙端 HTTP `8001`（`CHAO_KVM_HTTP_V2`），本機 Agent API 為 `127.0.0.1:45889`。請改看 `docs/KVM_NET472_PEER.md` 與 `docs/RECOVERY_20261016.md`；勿依本文件舊埠號修改連線。

這個專案目前使用已完成的 `D:\00-CODE\LanKVM` 作為實際 KVM runtime；
`D:\delta\ChaoChaoKvm` 的 C# / WebView2 介面仍是 G0/G1 重構中的前端，尚未接完整畫面傳輸。

## 啟動

雙擊：

`啟動_完整遠端KVM.bat`

## LAN / Wi-Fi

- Host 預設監聽 `0.0.0.0:45888`。
- 同一個區網內，控制端直接填 Host 的 LAN / Wi-Fi IPv4 與 `45888`。
- 這條路徑不依賴 ngrok，即使外網 tunnel 關閉也能使用。

## 外網 / ngrok

1. 在「設定」輸入「連線密碼」；Host 與控制端使用相同密碼。
2. Host 先按「分享本機」。
3. Host 按「外網分享」。
4. 程式會使用此 Windows 帳號既有的 ngrok config/authtoken，建立 TCP tunnel。
5. 狀態列顯示 `外網入口：host:port` 後，另一台電腦把該 host / port 填到連線欄位。
6. 控制端輸入相同「連線密碼」後連線。

外網模式不是取代 LAN，而是額外加入第二個入口：

`LAN/Wi-Fi -> 本機 TCP 45888`

`Internet -> ngrok TCP -> 本機 TCP 45888`

## 安全

- KVM payload 維持 X25519 + HKDF + AES-256-GCM。
- 新版 handshake 在有設定連線密碼時，額外使用 HMAC-SHA256 做雙向身分驗證。
- 「外網分享」在密碼空白時會拒絕啟動，避免把可控制桌面的 KVM port 裸露到公網。

