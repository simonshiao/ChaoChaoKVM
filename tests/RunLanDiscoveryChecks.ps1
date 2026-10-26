param([Parameter(Mandatory=$true)][string]$BuildDirectory, [string]$SelfAddress = '127.0.0.1')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $projectRoot 'src\ChaoChaoKvm.Net472'
$output = [IO.Path]::GetFullPath($BuildDirectory)
$appAssembly = Join-Path $output 'ChaoChaoKvm.exe'
if (-not (Test-Path -LiteralPath $appAssembly)) { throw 'Run RunUiChecks.ps1 with this output directory first.' }
& 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' /nologo /target:exe "/out:$output\LanDiscoveryTests.exe" "/reference:Kvm=$appAssembly" /reference:System.Web.Extensions.dll (Join-Path $PSScriptRoot 'LanDiscoveryTests.cs') (Join-Path $source 'Services\LanDiscoveryService.cs') (Join-Path $source 'Core\NetworkUtils.cs') (Join-Path $source 'Core\JsonUtil.cs') (Join-Path $source 'Core\Protocol.cs')
if ($LASTEXITCODE -ne 0) { throw 'LAN discovery test compilation failed' }
& (Join-Path $output 'LanDiscoveryTests.exe') $SelfAddress
if ($LASTEXITCODE -ne 0) { throw 'LAN name discovery checks failed' }
