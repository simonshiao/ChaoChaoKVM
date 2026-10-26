from pathlib import Path
import re

root = Path(__file__).resolve().parents[1]
protocol = (root / "src/ChaoChaoKvm.App/Protocol/LanKvmProtocol.cs").read_text(encoding="utf-8")
channel = (root / "src/ChaoChaoKvm.App/Transport/SecureChannel.cs").read_text(encoding="utf-8")
handshake = (root / "src/ChaoChaoKvm.App/Transport/SecureHandshake.cs").read_text(encoding="utf-8")

expected = {
    "DefaultPort": "45888",
    "Frame": "1",
    "Input": "2",
    "Info": "3",
    "Ping": "4",
    "Close": "5",
    "Cursor": "6",
    "FileMeta": "7",
    "FileChunk": "8",
    "FileEnd": "9",
    "ClipboardImage": "10",
}
for name, value in expected.items():
    assert re.search(rf"\b{name}\s*=\s*{value}\s*;", protocol), (name, value)

assert "128 * 1024 * 1024" in protocol
assert "256 * 1024" in protocol
assert "WriteUInt64BigEndian" in channel
for c in "LKV1":
    assert f"(byte)'{c}'" in channel
assert "AesGcm" in channel and "16" in channel
assert 'Encoding.ASCII.GetBytes("LanKVM-v1")' in handshake
assert "X25519PrivateKeyParameters" in handshake
assert "HKDF.DeriveKey" in handshake
assert "HashAlgorithmName.SHA256" in handshake
print("G1_CONTRACT_STATIC_PASS")
