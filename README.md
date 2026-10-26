# 吵吵 KVM

Windows 桌面 KVM 與檔案傳輸工具。區域網路畫面、輸入與傳檔沿用 HTTP/TCP 8001；Recovery 另包含 Cloudflare 網路中繼。

## 開發與啟動

- 目前母專案：本資料夾的 `ChaoChaoKvm.sln`。
- Recovery/LAN 原始碼：`src/ChaoChaoKvm.Net472`；介面在其 `Web` 子目錄。
- `src/ChaoChaoKvm.App` 與根目錄 `Web` 是保留的其他實作，不是這次 Recovery 建置所使用的介面。
- 成品安裝位置：`D:\0_軟體開發區\1.成品區\ChaoChaoKvm-Recovery` 與 E 槽的 Recovery 套件。原始碼修改不會自動替換已執行的成品。
- `--ui-preview` 可另開介面預覽，預覽不啟動 HTTP、探索、擷取或輸入服務。

## 用電腦名稱連線

1. 兩台電腦開啟新版 KVM，連到可以互通的 Wi-Fi 或 LAN。
2. 在「電腦名稱或 IP 位址」輸入對方 Windows 電腦名稱，例如 `Home-1`，或點選「附近的電腦」。
3. 程式會先找附近 KVM，再使用 Windows 名稱解析，確認連線埠可達後沿用原本的 KVM 連線流程。
4. 仍可直接輸入 IPv4 位址；Wi-Fi 與 VPN 同時啟用時，本機位址選單會顯示網卡名稱並優先選 Wi-Fi。

同名設備會要求從清單選擇；本機名稱與 IP 不接受作為正式遠端目標。名稱功能目前是同一個區域網路的探索，跨網路仍使用「網路連線」頁面。

附近探索使用 UDP 45890，每 10 秒搜尋並在 30 秒後移除沒有更新的位址，只公告名稱、隨機裝置 ID 與服務埠。它不交換控制金鑰，也不更改既有可信位址與控制權限。裝置 ID 只用來區分探索結果，不代表設備已通過身分驗證。VPN/TUN 不會被拿來送區域廣播。

若 Wi-Fi 隔離裝置或封鎖廣播，附近清單可能為空；Windows 能解析名稱時仍可使用名稱，否則改用 IP。舊版 KVM 沒有探索服務，需依賴 Windows 名稱解析或手動 IP。

## 建置及驗證

依賴 Windows .NET Framework、WPF、WebView2 Runtime，以及 NuGet 快取的 `Microsoft.Web.WebView2 1.0.3537.50`。目前腳本使用 .NET Framework MSBuild/csc，參考組件位置可見 `tests/RunUiChecks.ps1`。

建議將輸出放在測試實驗區，避免在母專案散落執行檔：

```powershell
.\tests\RunUiChecks.ps1 -OutputDirectory 'D:\0_軟體開發區\4.測試實驗區\KVM\20261026_名稱探索\lan'
.\tests\RunLanDiscoveryChecks.ps1 -BuildDirectory 'D:\0_軟體開發區\4.測試實驗區\KVM\20261026_名稱探索\lan'
```

`RunLanDiscoveryChecks.ps1 -SelfAddress <本機 Wi-Fi IPv4>` 可在該介面上驗證探索封包、名稱解析與實際 KVM HTTP 用戶端接收測試影格。單機自連不代表封包經過另一台設備或無線基地台。

建置 Recovery 版需加 MSBuild `/p:CloudflareBuild=true` 並明確指定 `/p:OutputPath=...`；未指定時專案既有設定會指向 E 槽 Recovery，開發時應避免直接覆寫執行中的成品。

`bin`、`obj`、`node_modules` 與測試建置輸出可重建。`docs/*backup*` 是保留的歷史回溯資料，清理前需先確認用途；`.git` 是版本資料庫。
