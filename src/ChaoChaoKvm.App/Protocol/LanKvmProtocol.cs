namespace ChaoChaoKvm.Protocol;

/// <summary>
/// 與舊版 Python app.py 封包編號保持一致。
/// G0 只固定契約，不在此 Gate 啟動網路或輸入注入。
/// </summary>
public static class LanKvmProtocol
{
    public const int DefaultPort = 45888;
    public const int MaxPacket = 128 * 1024 * 1024;
    public const int FileChunkSize = 256 * 1024;

    public const byte Frame = 1;
    public const byte Input = 2;
    public const byte Info = 3;
    public const byte Ping = 4;
    public const byte Close = 5;
    public const byte Cursor = 6;
    public const byte FileMeta = 7;
    public const byte FileChunk = 8;
    public const byte FileEnd = 9;
    public const byte ClipboardImage = 10;
}
