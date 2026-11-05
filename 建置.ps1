param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'Release\Recovery')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\ChaoChaoKvm.Net472\ChaoChaoKvm.Net472.csproj'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$exe = Join-Path $output 'ChaoChaoKvm.exe'
$refCandidates = @(
    'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.7.2',
    'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.6.2'
)
$frameworkPath = $refCandidates |
    Where-Object { Test-Path -LiteralPath (Join-Path $_ 'mscorlib.dll') -PathType Leaf } |
    Select-Object -First 1
$msbuildCandidates = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\MSBuild.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe')
)
$msbuild = $msbuildCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1

if (-not (Test-Path -LiteralPath $project -PathType Leaf)) { throw "找不到專案：$project" }
if (-not $frameworkPath) { throw '找不到 .NET Framework 4.6.2/4.7.2 參考組件。' }
if (-not $msbuild) { throw '找不到 .NET Framework MSBuild。' }

$running = Get-CimInstance Win32_Process -Filter "name='ChaoChaoKvm.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.ExecutablePath -and [IO.Path]::GetFullPath($_.ExecutablePath) -ieq [IO.Path]::GetFullPath($exe) }
if ($running) { throw "Release 版仍在執行，請先正常關閉後再建置：$exe" }

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$artifacts = Join-Path $tempRoot ('ChaoChaoKvmBuild-' + [Guid]::NewGuid().ToString('N'))
$intermediate = Join-Path $artifacts 'obj'

try {
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    & $msbuild $project /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU /p:CloudflareBuild=true `
        "/p:OutputPath=$output\" "/p:IntermediateOutputPath=$intermediate\" "/p:FrameworkPathOverride=$frameworkPath" /verbosity:minimal
    if ($LASTEXITCODE -ne 0) { throw "Recovery Release 建置失敗，MSBuild 結束碼：$LASTEXITCODE" }
    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "建置完成但找不到執行檔：$exe" }
    Write-Host "Recovery Release 建置完成：$exe"
}
finally {
    $resolvedArtifacts = [IO.Path]::GetFullPath($artifacts)
    if ([IO.Path]::GetDirectoryName($resolvedArtifacts) -ine $tempRoot -or
        [IO.Path]::GetFileName($resolvedArtifacts) -notmatch '^ChaoChaoKvmBuild-[a-f0-9]{32}$') {
        throw '暫存建置目錄不符合預期，已取消清理。'
    }
    if (Test-Path -LiteralPath $resolvedArtifacts) {
        Remove-Item -LiteralPath $resolvedArtifacts -Recurse -Force
    }
}
