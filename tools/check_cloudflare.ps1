param(
    [string[]]$Hostnames = @('kvm-relay.chaochao.win', 'kvm.chaochao.win')
)

# Agent / Codex 的唯讀預檢：只呈現是否有設定，不輸出 Tunnel Token、Room ID、配對金鑰。
$ErrorActionPreference = 'Stop'
$configPath = Join-Path $env:LOCALAPPDATA 'ChaoChaoKvm\cloudflare.json'
$settings = $null
if (Test-Path -LiteralPath $configPath -PathType Leaf) {
    $settings = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
$localTool = Join-Path (Split-Path -Parent $PSScriptRoot) 'cloudflared.exe'
if (-not (Test-Path -LiteralPath $localTool)) {
    $localTool = Join-Path $PSScriptRoot '..\Release\Recovery\cloudflared.exe'
}
$command = Get-Command 'cloudflared.exe' -ErrorAction SilentlyContinue
$installed = @( $localTool, 'C:\Program Files (x86)\cloudflared\cloudflared.exe', $command.Source ) |
    Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } |
    Select-Object -First 1
$listeners = @()
try {
    $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop |
        Where-Object { $_.LocalPort -in @(8001, 45889) } |
        Select-Object LocalAddress, LocalPort, OwningProcess)
} catch { }
$endpoints = @()
foreach ($hostName in $Hostnames) {
    $status = 'unreachable'
    $accessLogin = $false
    try {
        $response = Invoke-WebRequest -Uri ('https://' + $hostName + '/') -UseBasicParsing -TimeoutSec 6
        $status = [string][int]$response.StatusCode
        $accessLogin = [string]$response.Content -match 'Cloudflare Access'
    }
    catch {
        if ($_.Exception.Response) { $status = [string][int]$_.Exception.Response.StatusCode }
    }
    $endpoints += [pscustomobject]@{ Hostname = $hostName; HttpStatus = $status; AccessLoginPage = $accessLogin }
}
[pscustomobject]@{
    Machine = $env:COMPUTERNAME
    KvmPortListeners = $listeners
    CloudflaredAvailable = [bool]$installed
    CloudflaredSignature = if ($installed) { [string](Get-AuthenticodeSignature -LiteralPath $installed).Status } else { 'missing' }
    CloudflareConfigPresent = [bool]$settings
    NamedTunnelConfigured = [bool]($settings -and $settings.tunnel_token_protected)
    RelayConfigured = [bool]($settings -and $settings.relay_room_id -and $settings.relay_secret_protected)
    RelayHostname = if ($settings) { $settings.relay_hostname } else { '' }
    Endpoints = $endpoints
    Note = 'HTTP 200 Cloudflare Access login page only proves HTTPS and Access routing, not a working KVM WebSocket relay.'
} | ConvertTo-Json -Depth 6
