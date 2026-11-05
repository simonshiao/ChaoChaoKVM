# KVM 名稱連線交接（2026-10-26）

## 2026-11-05 晚間：Computer Use 遠端手腳

- 2026-11-05 晚間補強：正式建置 `建置.ps1` 會將 `AGENTS.md`、`docs/COMPUTER_USE.md` 和 `tools/kvm_computer.py` 一起複製進 `Release/Recovery`，避免獨立拷貝套件後 Agent 找不到入口。保留單一原始來源，不建立第二套 MCP Runtime。
- 使用者已決定不為 KVM 建立 PowerShell／MCP／Agent；CHAO MCP Hub 和 Codex 繼續沿用既有工具，由 KVM 提供遠端畫面與鍵鼠輸入。
- 既有 loopback LocalAgentApi (127.0.0.1:45889) 擴充八個 `computer_*` 指令：狀態、截圖、滑鼠、按鍵、組合鍵、Unicode 文字；使用既有 KVM HTTP 8001／反向佇列，沒有開新的網路埠。
- Python CLI：`tools/kvm_computer.py`；實作：`Api/KvmComputerUseAdapter.cs`、`Api/LocalAgentApi.cs`、`Services/InputInjectionService.cs`。詳見 `docs/COMPUTER_USE.md`。
- 注意：`queued=true` 僅代表已送出/排隊，須再截圖驗證遠端真的操作；Unicode 需要遠端也更新。
- 正式 Recovery 已在 @CHAO2 正常關閉舊 PID 21452 後，用既有 `建置.ps1`、`啟動.vbs` 重建啟動；新 PID **34640**（2026/11/05 17:43），實測 TCP 8001／127.0.0.1:45889 都由新程序監聽。
- 實機 CLI `computer_status` 成功回傳 `192.168.1.4 / TWCYNNB1302 / 1920×1080`；`computer_screenshot` 取得並解碼真實 JPEG。實驗輸出位置：`E:\0_軟體開發區\4.測試實驗區\KVM\20261105_computeruse`。
- `computer_click` 已送入 KVM queue，但當時遠端是 Windows 鎖定畫面，點擊前後畫面沒有可驗證變化；**鍵鼠實際生效與 Unicode 跨機仍待遠端解鎖／同步新版驗收**，不能宣稱全通過。
- 隔離測試編譯、UI 行為 39 項、LAN 43 項、Python 解析與 5 個異常拒絕測試均通過；尚未驗證 Codex 直接調用的完整 workflow，現階段由既有 `exec_command` 執行 CLI。

## 2026-11-05 下午接手：Recovery 正式版已更新並啟動

- 由 @CHAO2 直接檢查：Git `main` 先前最新提交 `9d18827` 已包含 Recovery 固定 8001，接手時工作目錄乾淨。
- 先透過主視窗正常關閉 Recovery PID 17772，再執行根目錄 `建置.ps1` 和 `啟動.vbs`；2026/11/05 16:01 新版啟動為 PID 21452，確認 TCP 0.0.0.0:8001、UDP 0.0.0.0:45890 均由此程序監聽。
- 新增相容性診斷：附近清單不再隱藏不相容埠號的探索公告，會標示對方 HTTP 埠、停用連線按鈕；接受舊版 port 80 探索訊息，但仍嚴格拒絕以非 8001 埠建立一般 Recovery 名稱連線，不變更控制授權。
- 已跑 `tests/RunUiChecks.ps1`：39 項 UI 行為及 Web state 通過；`tests/RunLanDiscoveryChecks.ps1`：loopback 42 項、實體 Wi-Fi 192.168.1.3 上的單機探索 43 項通過。測試輸出在 `E:\0_軟體開發區\4.測試實驗區\KVM\20261105_不相容埠診斷`。
- HOME-1 曾有 192.168.1.4 連入 HTTP 8001 的 TCP 連線，但反向檢查 192.168.1.4 的 TCP 80、8001、18001 均無法連入，ICMP 也未回應；此 IP 是區網裝置，**尚無法證實就是公司 TW 主機**。ARP 可解析；未完成跨機 KVM 畫面互通驗收。
- HOME-1 的 Windows 防火牆三個 Profile 當下皆停用；舊規則 TCP 45888 不是此台當前阻擋原因。網卡設定檔為 Public，已觀察 UDP 5353 端點。
- 使用者表示 @DELTA2 與 @CHAO3 已開啟，但 MCP 直接呼叫仍分別回傳 HTTP 429（openai.org）及 HTTP 405（chaochao.win），目前無法進入公司主機讀取其實際 KVM 版本、監聽埠與防火牆。**不要把 MCP 連線失敗直接等同 KVM 故障**。
- 下一步：公司 MCP 通道恢復後，先讀 TW 的 `ChaoChaoKvm.exe` 真實執行路徑與 PID、TCP 8001／UDP 45890／5353 監聽、KVM 版本與防火牆；再由雙端測試 TCP 8001 和探索。勿猜測 192.168.1.4 身分或修改其他程序。

## 2026-11-05 Release 位置與入口

- Recovery 正式版固定建置於專案內 `Release\Recovery\`。
- 根目錄 `建置.ps1` 負責建置；完成後以根目錄 `啟動.vbs` 啟動。
- MSBuild 中間檔使用獨立暫存目錄，避免散落在原始碼樹；`Release\Recovery` 由 Git 忽略。
- 本地 KVM 圖示改由 `src\ChaoChaoKvm.Net472\Assets\ChaoChaoKvm.ico` 提供，不再依賴專案外的素材路徑。
- 已使用根目錄建置腳本成功產生 `Release\Recovery\ChaoChaoKvm.exe`，確認 Web、WebView2 DLL 與原生 Loader 都在套件中；目前舊測試版仍執行中，因此未啟動新版。
- Recovery/LAN 固定 HTTP 8001；啟動時忽略舊設定檔的 `listen_port`，避免舊的 80 埠設定讓 mDNS/UDP 廣播被同埠篩選隱藏。Online 版仍沿用自己的設定埠。

## 2026-11-04 區網 mDNS 探索補強

- 新增 KVM 專用 DNS-SD 服務 `_chaokvm._tcp.local.`，透過 mDNS（UDP 5353、`224.0.0.251`）公告名稱、服務埠與隨機裝置 ID。
- mDNS 僅加入上線中的實體 Wi-Fi／Ethernet 介面，定期更新介面，與既有 UDP 45890 定向廣播並行；VPN/TUN 不參與。
- mDNS 結果併入既有附近清單、同名處理及 HTTP 8001 連線流程；不建立可信設備、不交換控制金鑰。mDNS 綁定或網路不可用時保留 UDP／Windows DNS 路徑。
- Recovery/Cloudflare Release 編譯成功；區網探索檢查 40 項通過，包括 mDNS 同機 Wi-Fi 探索與 UDP 定向廣播。仍需第二台實體電腦驗收多播互通。
- 驗證輸出：`E:\0_軟體開發區\4.測試實驗區\KVM\20261104_KvmMdnsDiscovery\recovery`。

## 已完成

- 母專案仍是本資料夾；先前 Wi-Fi 網卡標籤、優先選取與獨立 UI 預覽變更一併保留。
- Recovery/LAN 可輸入 Windows 電腦名稱、`.local` 名稱或 IPv4。
- UDP 45890 定向區域廣播與 mDNS DNS-SD 查詢／回覆、近期設備清單、同名設備提示、過期移除。
- 名稱解析先用探索結果，再使用 Windows DNS；探測採 TCP 連線，沒有呼叫會更新可信設備狀態的 HTTP health。
- 已選定的目的地仍交給既有 HTTP KVM 流程，不改畫面、輸入、檔案或 Cloudflare 中繼協定。
- 裝置 ID 存在使用者既有設定中，只作探索去重，不當作授權或已驗證身分。
- Online 共用專案已加入新的服務檔；補齊原本漏列的 CloudflareRelayService 編譯項目，Online 不啟動區域探索。

## 驗證

- `tests/RunUiChecks.ps1`：Release 編譯、39 項原生 UI 行為與網頁狀態檢查通過。
- `tests/RunLanDiscoveryChecks.ps1`：loopback 37 項、Wi-Fi 位址 38 項通過。
- 驗證涵蓋 UDP query/reply、Wi-Fi 定向廣播回覆、依名稱建立正式 HTTP 連線並接收測試影格、同名設備、錯誤埠、無效資料、來源位址限制、過期、取消、DNS fallback、本機／非區網位址拒絕。
- LAN、Recovery/Cloudflare、Online 三種組態編譯通過。既有編譯警告仍包括建置分支不可達程式碼與 Relay 的未等待非同步工作。
- 已實際開啟新版 Recovery UI 預覽，確認名稱欄、複製名稱、附近清單與 Wi-Fi 選單排版。

## 輸出及紀錄

`D:\0_軟體開發區\4.測試實驗區\KVM\20261026_名稱探索`

- `lan/`：UI／探索測試與日誌。
- `recovery/`：含 Cloudflare 功能的新版測試套件與 build.log。
- `online/`：Online 相容性編譯與 build.log。
- `ui-checks.log`：UI 驗證紀錄。

測試輸出放在母專案外，不要再把 EXE、DLL、node_modules 或測試快取複製回母專案根目錄。

## 尚待驗收／後續範圍

- 仍需第二台實體電腦驗證真實 Wi-Fi 互連。單機自連與廣播回覆不等同於經過第二台設備或無線基地台的驗收。
- 同帳號跨網路名稱目錄尚未實作；跨網路仍走既有連線碼與 Cloudflare 中繼。
- 若 Wi-Fi 隔離用戶端或封鎖多播／廣播，探索清單可能為空；可依賴 Windows 名稱解析或 IP。
- 目前已執行的 D/E Recovery 成品沒有被覆寫。新版預覽使用 `--ui-preview`，不啟動連線服務；提交與推送狀態以 Git 工作目錄及遠端 `main` 為準。

主要新檔為 `Services/LanDiscoveryService.cs`、`tests/LanDiscoveryTests.cs` 與其 runner。共享入口與 GUI 變更在 `MainWindow.xaml.cs`、`Core/AppConfig.cs`、`Core/NetworkUtils.cs`、`Web/`。不需要重做先前的 GitHub 初始上傳或整包清理。
