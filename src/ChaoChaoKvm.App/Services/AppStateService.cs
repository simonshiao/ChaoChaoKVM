using ChaoChaoKvm.Models;

namespace ChaoChaoKvm.Services;

public sealed class AppStateService
{
    private readonly object _sync = new();
    private readonly AppState _state = new();

    public AppState Snapshot()
    {
        lock (_sync)
        {
            return new AppState
            {
                AppName = _state.AppName,
                Version = _state.Version,
                Status = _state.Status,
                StatusText = _state.StatusText,
                HostRunning = _state.HostRunning,
                ClientConnected = _state.ClientConnected,
                TargetIp = _state.TargetIp,
                Port = _state.Port,
                Quality = _state.Quality,
                Fps = _state.Fps,
                AllowInput = _state.AllowInput
            };
        }
    }

    public void StartHost()
    {
        lock (_sync)
        {
            _state.HostRunning = true;
            _state.Status = "hosting";
            _state.StatusText = "分享服務準備中 · G0 UI 模式";
        }
    }

    public void StopHost()
    {
        lock (_sync)
        {
            _state.HostRunning = false;
            _state.Status = "standby";
            _state.StatusText = "分享已停止";
        }
    }

    public void SetTarget(string ip, int port)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port), "連接埠必須介於 1 到 65535。");

        lock (_sync)
        {
            _state.TargetIp = ip.Trim();
            _state.Port = port;
            _state.StatusText = string.IsNullOrWhiteSpace(_state.TargetIp)
                ? "請輸入對方 IP"
                : $"目標 {_state.TargetIp}:{port}";
        }
    }
}
