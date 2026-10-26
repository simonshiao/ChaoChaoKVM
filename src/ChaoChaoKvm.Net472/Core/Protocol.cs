namespace ChaoChaoKvm.Core
{
    public static class Protocol
    {
        public const string AppName = "ChaoChaoKvm";
        public const string Version = "1.1-net472-http";
        public const string ProtocolName = "CHAO_KVM_HTTP_V2";
        public const int DefaultPort = 8001;
        public const int LegacyKvmPort = 45888;
        public const int DefaultAgentPort = 45889;
        public const int BufferSize = 64 * 1024;
        public const int MaxJsonBytes = 2 * 1024 * 1024;
        public const int MaxFrameBytes = 24 * 1024 * 1024;
        public const int MaxPacketBytes = MaxFrameBytes;
        public const byte Frame = 1;
        public const byte Input = 2;
        public const byte Info = 3;
        public const byte Ping = 4;
        public const byte Close = 5;
    }
}
