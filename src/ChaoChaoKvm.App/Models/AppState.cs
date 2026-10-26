namespace ChaoChaoKvm.Models;

public sealed class AppState
{
    public string AppName { get; init; } = "ChaoChao KVM";
    public string Version { get; init; } = "0.1.0-g0";
    public string Status { get; set; } = "standby";
    public string StatusText { get; set; } = "待命";
    public bool HostRunning { get; set; }
    public bool ClientConnected { get; set; }
    public string TargetIp { get; set; } = "";
    public int Port { get; set; } = 45888;
    public int Quality { get; set; } = 65;
    public int Fps { get; set; } = 18;
    public bool AllowInput { get; set; } = true;
}
