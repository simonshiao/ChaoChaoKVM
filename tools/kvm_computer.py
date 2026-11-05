#!/usr/bin/env python3
"""CHAO KVM Computer Use：共用現有 KVM 本機 API，供 ChatGPT / Codex 呼叫。"""
import argparse
import base64
import json
import socket
import sys
from pathlib import Path


def 呼叫本機KVM(參數, 連接埠):
    """只連線至 127.0.0.1，不開設另一個遠端 Agent 或網路服務。"""
    with socket.create_connection(("127.0.0.1", 連接埠), timeout=8) as 連線:
        連線.settimeout(20)
        封包 = json.dumps(參數, ensure_ascii=False).encode("utf-8") + b"\n"
        連線.sendall(封包)
        with 連線.makefile("rb") as 回覆:
            資料 = 回覆.readline(6 * 1024 * 1024)
    if not 資料 or not 資料.endswith(b"\n"):
        raise RuntimeError("KVM API 沒有回覆完整的單行 JSON。")
    結果 = json.loads(資料)
    if not 結果.get("ok", False):
        raise RuntimeError(結果.get("error", "KVM 操作失敗"))
    return 結果


def 主要():
    解析器 = argparse.ArgumentParser(description="使用現有 KVM 作為 Computer Use 的遠端鍵鼠與畫面")
    解析器.add_argument("動作", choices=[
        "status", "screenshot", "click", "move", "scroll", "key", "hotkey", "type", "help"])
    解析器.add_argument("--peer", help="遠端 KVM IP；省略時使用 KVM 目前連線目標")
    解析器.add_argument("--port", type=int, default=45889, help="KVM 本機 Agent API 埠")
    解析器.add_argument("--x", type=int, help="遠端畫面像素 X")
    解析器.add_argument("--y", type=int, help="遠端畫面像素 Y")
    解析器.add_argument("--button", choices=["left", "right", "middle"], default="left")
    解析器.add_argument("--clicks", type=int, default=1)
    解析器.add_argument("--delta", type=int)
    解析器.add_argument("--key", help="單一按鍵，例如 enter、f5")
    解析器.add_argument("--keys", nargs="+", help="組合鍵，例如 ctrl a")
    解析器.add_argument("--text", help="要輸入的 Unicode 文字")
    解析器.add_argument("--out", default="kvm-remote.jpg", help="截圖輸出位置")
    參數 = 解析器.parse_args()
    命令 = {"cmd": "help" if 參數.動作 == "help" else "computer_" + 參數.動作}
    if 參數.peer:
        命令["peer"] = 參數.peer
    if 參數.動作 in ("click", "move", "scroll"):
        if 參數.x is None or 參數.y is None:
            解析器.error("滑鼠操作必須提供 --x 與 --y（遠端截圖的原始像素座標）")
        命令.update(x=參數.x, y=參數.y)
    if 參數.動作 == "click":
        命令.update(button=參數.button, clicks=參數.clicks)
    if 參數.動作 == "scroll":
        if 參數.delta is None:
            解析器.error("scroll 必須指定 --delta")
        命令["delta"] = 參數.delta
    if 參數.動作 == "key":
        if not 參數.key:
            解析器.error("key 必須指定 --key")
        命令["key"] = 參數.key
    if 參數.動作 == "hotkey":
        if not 參數.keys:
            解析器.error("hotkey 必須指定 --keys，例如 --keys ctrl a")
        命令["keys"] = 參數.keys
    if 參數.動作 == "type":
        if not 參數.text:
            解析器.error("type 必須指定 --text")
        命令["text"] = 參數.text
    結果 = 呼叫本機KVM(命令, 參數.port)
    if 參數.動作 == "screenshot":
        路徑 = Path(參數.out).expanduser().resolve()
        路徑.parent.mkdir(parents=True, exist_ok=True)
        路徑.write_bytes(base64.b64decode(結果.pop("image_base64"), validate=True))
        結果["file"] = str(路徑)
    print(json.dumps(結果, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    try:
        主要()
    except (OSError, RuntimeError, ValueError, json.JSONDecodeError) as 錯誤:
        print(json.dumps({"ok": False, "error": str(錯誤)}, ensure_ascii=False), file=sys.stderr)
        sys.exit(1)
