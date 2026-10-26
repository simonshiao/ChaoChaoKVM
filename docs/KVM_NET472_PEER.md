# ChaoChao KVM — 純 C# / .NET Framework 4.7.2

正式 Runtime：`D:\delta\ChaoChaoKvm\ChaoChaoKvm.exe`

## 核心架構

- 同一支 EXE 同時具備 KVM、傳檔、JSON 與 Agent API。
- 網路核心正式改用先前 ChaoTransfer 已驗證的 HTTP 8001 架構。
- Server：`TcpListener + 自建 HTTP/1.1`，不依賴 HttpListener / URLACL / Kestrel。
- Client：`HttpWebRequest` 主動 outbound。
- 每台開啟後都會監聽 `0.0.0.0:8001`，並嘗試主動連已知對端。
- 公司 inbound 被擋時不影響雙向操作：公司端只要能 outbound，就可 push 自己資料並 polling / pull 家端 queue。
- 舊 KVM `TCP 45888` 不再是正式傳輸核心。

## KVM over HTTP

協定：`CHAO_KVM_HTTP_V2`

- `PUT /api/v1/kvm/push/frame`：主動端推送自己的 JPEG 桌面。
- `GET /api/v1/kvm/pull/frame?after=<seq>`：主動端拉取對方最新 JPEG 桌面。
- `POST /api/v1/kvm/push/input`：直接推送滑鼠 / 鍵盤事件。
- `GET /api/v1/kvm/pull/input`：當反向 inbound 不通時，由主動端拉回對方排隊的控制事件。
- `GET /api/v1/health`、`POST /api/v1/heartbeat`：健康檢查與連線狀態。

因此只有單一方向可建立 HTTP request 時，兩邊仍可互相看畫面與操作。

## 傳檔 / JSON

完整沿用 ChaoTransfer 的已驗證 API：

- `PUT /api/v1/push/file`
- `POST /api/v1/push/json`
- `GET /api/v1/pull/next`
- `GET /api/v1/pull/file/{id}`
- `POST /api/v1/pull/ack/{id}`

檔案本體使用 `application/octet-stream`，不塞 Base64 JSON。

若本機 outbound 可通，直接 PUT 到對方；若 outbound 不通，檔案加入本機 queue，等另一台主動 GET 回去。

## Agent API

本機 JSONL：`127.0.0.1:45889`

- `{"cmd":"status"}`
- `{"cmd":"connect","ip":"169.254.246.222"}`
- `{"cmd":"retry"}`
- `{"cmd":"ping_peer"}`
- `{"cmd":"allow_control","enabled":true}`
- `{"cmd":"input","payload":{"kind":"move","x":"0.5","y":"0.5"}}`
- `{"cmd":"send_json","payload":{"action":"hello"}}`
- `{"cmd":"pull_json","channel":"delta-http-v1.request","wait_ms":1000}`
- `{"cmd":"send_file","paths":["D:\\Data\\result.zip"]}`
- `{"cmd":"set_receive_dir","path":"D:\\Receive","overwrite":false}`

## 已驗證

- .NET Framework 傳統 MSBuild：0 warning / 0 error。
- 啟動後只監聽 HTTP `8001` 與本機 Agent `45889`；舊 `45888` 不再監聽。
- `GET /api/v1/health`：PASS。
- KVM frame HTTP pull：PASS，1920×1080 JPEG 可取得。
- HTTP binary file PUT：PASS。
- 收到檔案與來源檔做 `fc /b`：完全一致。

