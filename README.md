# 吵吵 KVM

Windows 桌面 KVM 與檔案傳輸工具。區域網路畫面、輸入與傳檔沿用 HTTP/TCP 8001；Recovery 另包含 Cloudflare 網路中繼。

Recovery/LAN 固定使用 HTTP 8001；升級時會忽略舊設定檔中的其他埠號，避免區網探索因兩端埠號不同而漏掉設備。Online 版維持獨立埠號。

## 開發與啟動

- 目前母專案：本資料夾的 `ChaoChaoKvm.sln`。
- Recovery/LAN 原始碼：`src/ChaoChaoKvm.Net472`；介面在其 `Web` 子目錄。
- `src/ChaoChaoKvm.App` 與根目錄 `Web` 是保留的其他實作，不是這次 Recovery 建置所使用的介面。
- Recovery 正式版統一建置在本資料夾的 `Release\Recovery`；原始碼修改不會自動替換目前已開啟的程式。
- `--ui-preview` 可另開介面預覽，預覽不啟動 HTTP、探索、擷取或輸入服務。

## 建置與啟動

在 PowerShell 進入本資料夾後執行 `.\建置.ps1`，或直接雙擊根目錄的 `建置.ps1`。建置完成後，雙擊根目錄的 `啟動.vbs` 開啟正式版。

- Release 執行檔：`Release\Recovery\ChaoChaoKvm.exe`
- `Web` 介面、WebView2 SDK DLL 與 Loader 一併放在 Release 資料夾；Evergreen WebView2 Runtime 本身仍需安裝在目標電腦。使用者不需要另外安裝 Edge 瀏覽器。
- 若要在沒有 WebView2 Runtime 的電腦直接執行，需改用 Fixed Version app-local 部署；官方 Runtime 套件超過 250 MB，且需由專案自行更新。
- 建置腳本將中間檔放在 Windows 暫存目錄，並在完成後清除；若 Release 版仍開著，會先停止建置並提示關閉程式。

## 用電腦名稱連線

1. 兩台電腦開啟新版 KVM，連到可以互通的 Wi-Fi 或 LAN。
2. 在「電腦名稱或 IP 位址」輸入對方 Windows 電腦名稱，例如 `Home-1`，或點選「附近的電腦」。
3. 程式會先找附近 KVM，再使用 Windows 名稱解析，確認連線埠可達後沿用原本的 KVM 連線流程。
4. 仍可直接輸入 IPv4 位址；Wi-Fi 與 VPN 同時啟用時，本機位址選單會顯示網卡名稱並優先選 Wi-Fi。

同名設備會要求從清單選擇；本機名稱與 IP 不接受作為正式遠端目標。名稱功能目前是同一個區域網路的探索，跨網路仍使用「網路連線」頁面。

附近探索同時使用 UDP 45890 定向廣播與 mDNS DNS-SD（UDP 5353、`224.0.0.251`、服務類型 `_chaokvm._tcp.local.`）。mDNS 只在實體 Wi-Fi／Ethernet 網卡上公布 KVM 名稱、HTTP 服務埠與隨機裝置 ID；VPN/TUN 不參與探索。每 10 秒搜尋，30 秒沒有更新就從附近清單移除。mDNS 不可用時，原有 UDP 探索與 Windows 名稱解析仍會運作。

若附近找到舊版（例如 HTTP 80）或其他非 8001 埠的 KVM，清單會標示「埠號不相容，請更新對方 KVM」並停用該設備的連線按鈕；此提示只代表收到探索公告，不代表對方 TCP 可連線。請更新對方到同埠版本再嘗試，勿直接改用不相容埠連線。

探索封包不交換控制金鑰，也不更改既有可信位址與控制權限。裝置 ID 只用來區分探索結果，不代表設備已通過身分驗證。

若 Wi-Fi 隔離裝置、封鎖多播／廣播或防火牆阻擋探索封包，附近清單可能為空；Windows 能解析名稱時仍可使用名稱，否則改用 IP。mDNS 僅用於可互通的區域網路探索，不提供跨網路名稱目錄。舊版 KVM 沒有探索服務，需依賴 Windows 名稱解析或手動 IP。

## 測試建置及驗證

依賴 Windows .NET Framework、WPF、Evergreen WebView2 Runtime，以及 NuGet 快取的 `Microsoft.Web.WebView2 1.0.3537.50`。目前腳本使用 .NET Framework MSBuild/csc，參考組件位置可見 `tests/RunUiChecks.ps1`。

測試輸出仍放在測試目錄；正式版請使用上面的 `建置.ps1` 和 `啟動.vbs`：

```powershell
.\tests\RunUiChecks.ps1 -OutputDirectory 'D:\0_軟體開發區\4.測試實驗區\KVM\20261026_名稱探索\lan'
.\tests\RunLanDiscoveryChecks.ps1 -BuildDirectory 'D:\0_軟體開發區\4.測試實驗區\KVM\20261026_名稱探索\lan'
```

`RunLanDiscoveryChecks.ps1 -SelfAddress <本機 Wi-Fi IPv4>` 可在該介面上驗證探索封包、名稱解析與實際 KVM HTTP 用戶端接收測試影格。單機自連不代表封包經過另一台設備或無線基地台。

正式建置會啟用 Cloudflare Recovery 組態，預設輸出到專案內的 `Release\Recovery`，不會寫入 D 槽成品資料夾或測試實驗區。

`bin`、`obj`、`node_modules` 與測試建置輸出可重建。`docs/archive` 收納舊版操作紀錄，`docs/*backup*` 保留歷史回溯資料；`.git` 是版本資料庫。
