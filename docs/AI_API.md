# ChaoChaoKvm AI Computer API (MVP)

The first AI-control gate is intentionally local-only and small.

- Bind address: `127.0.0.1:45889`
- Status: `GET /api/v1/status`
- Action: `POST /api/v1/action`
- Content type: `application/json`

Supported actions:

```json
{"action":"system.ping"}
```

```json
{"action":"computer.open_text_file","path":"D:\\delta\\ChaoChaoKvm\\AI_API_TEST.txt"}
```

```json
{"action":"computer.type","text":"hello"}
```

`computer.open_text_file` is deliberately restricted to `.txt` files in this MVP. The API does not expose arbitrary shell commands.
