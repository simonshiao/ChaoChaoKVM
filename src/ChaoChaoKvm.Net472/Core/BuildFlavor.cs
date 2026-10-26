namespace ChaoChaoKvm.Core
{
    public static class BuildFlavor
    {
#if CLOUDFLARE_BUILD
        public const bool IsCloudflare = true;
#else
        public const bool IsCloudflare = false;
#endif
#if ONLINE_BUILD
        public const bool IsOnline = true;
        public const string ProductName = "ChaoChao KVM Online";
#else
        public const bool IsOnline = false;
        public const string ProductName = "ChaoChao KVM";
#endif
    }
}
