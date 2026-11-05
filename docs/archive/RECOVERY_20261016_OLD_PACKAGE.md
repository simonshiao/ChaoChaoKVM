> 歷史紀錄：內容描述 2026-10-16 的舊 Recovery 套件與 D/Z 槽現場驗收，不是目前的建置或啟動指示。現行流程請看專案根目錄 README.md。

# 吵吵 KVM HTTP 8001 舊成果修復

日期：2026-10-16

本資料夾是 **舊版 ChaoChaoKvm 的單實例修復編譯包**，不是 Chao Computer V33；不覆蓋原本 `ChaoChaoKvm\ChaoChaoKvm.exe`。

## 修改內容

1. LAN KVM 新增單實例 Mutex，避免同一台電腦重複執行後搶占 TCP 8001；Online 使用獨立名稱，不影響 LAN。
2. 發生 AddressAlreadyInUse 時，說明真正原因可能是另一份吵吵 KVM 或 ChaoTransfer，占用 TCP 8001。
3. 保留原版 `KvmPeerManager` HTTP 8001、反向 frame/input pull、binary 檔案交換與既有 Web 介面，不改傳輸架構或設定資料。

## 2026-10-16 現場診斷

- TW `TWCYNNB1302` 曾同時執行原版 PID `63080`、`56040`；第一份擁有 `0.0.0.0:8001`，第二份沒有監聽。第二份已透過正常 UI 關閉；再確認只剩 PID `63080`，且 8001 仍由它監聽。
- HOME 截圖提示「HTTP 8001 無法啟動」，但 HOME 8001 的實際占用進程尚未完成驗證。HOME 端進程／連線列舉的工具呼叫被安全檢查阻擋，因此不能直接判定是 ChaoTransfer 或另一個舊 KVM。
- 修復版 Release 成功，HOME `--ui-preview` 可開啟 Web UI，連開第二份時出現「吵吵 KVM 已開啟」而非搶占 HTTP 埠；已關閉測試預覽，不曾開始螢幕擷取。
- 修復版完整執行檔及 Web 介面可從 HOME D: 與 TW Z: 讀取。

## 下一次正式切換（需分開驗收）

1. 先正常結束 HOME、TW 兩台電腦上所有 **原版 ChaoChaoKvm**；如有 ChaoTransfer 或其他 8001 服務，先確認用途及當前連線再決定是否停止。不能僅憑錯誤訊息就終止未知進程。
2. HOME 從 `D:\0_軟體開發區\1.成品區\ChaoChaoKvm-Recovery\ChaoChaoKvm.exe` 開啟一次。
3. TW 在授權的本機互動桌面從 `Z:\0_軟體開發區\1.成品區\ChaoChaoKvm-Recovery\ChaoChaoKvm.exe` 開啟一次，不能同時保留原版的 8001 Listener。
4. HOME 遠端位址填 `169.254.246.221`；TW 對端位址填 `169.254.246.222`，先確認每端 HTTP 8001 正常，再驗證 HOME 能看到與操作 TW 的真桌面。
5. 真正的 TW 螢幕擷取與遠端鍵鼠仍需來源端在公司核准的可見工作階段中自行啟動，先前工具安全檢查阻擋的操作不得用替代工具繞過。

## 本輪後續實測補充

- HOME 原先報錯的舊 KVM 視窗在後續檢查時已不存在；**沒有強制結束不明的 HOME 程序**。
- 從 HOME 啟動 `ChaoChaoKvm-Recovery\ChaoChaoKvm.exe` 正常模式，視窗 PID `31508`；透過實際 UI 檢查，介面顯示 **「HOME-1 · 可供連線」**，不再顯示先前 HTTP 8001 無法啟動的錯誤。
- TW 留下唯一一份原版 PID `63080`，仍監聽 TCP `0.0.0.0:8001`；後來誤開的 PID `56040` 已正常關閉。
- 尚未取得 HOME 實際遠端操控 TW 桌面的畫面與鍵鼠驗收證據，也未擅自啟動或繞過公司端的螢幕擷取工具限制。

**目前狀態：** HOME 修復版主機介面顯示「可供連線」、TW 原版 HTTP 8001 正在監聽；多開防護已實測。請先保留雙方現有程序與原版回退檔，後續在來源端獲准的分享工作階段中驗收實際雙機畫面。
