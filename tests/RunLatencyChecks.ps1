param([string]$ProjectRoot=(Split-Path -Parent $PSScriptRoot),[string]$BaselineExe='')
$ErrorActionPreference='Stop'
$output=Join-Path $ProjectRoot 'tests\latency-build'
$refDir='C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.6.2'
& 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe' (Join-Path $ProjectRoot 'src\ChaoChaoKvm.Net472\ChaoChaoKvm.Net472.csproj') /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU "/p:OutputPath=$output\" "/p:FrameworkPathOverride=$refDir" /verbosity:minimal
if($LASTEXITCODE -ne 0){throw 'Build failed'}
$csc='C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
foreach($test in @('LatencyBench','QueueTests','CompatibilityProbe')) {
 & $csc /nologo "/out:$output\$test.exe" "/reference:$output\ChaoChaoKvm.exe" (Join-Path $ProjectRoot ('tests\'+$test+'.cs'))
 if($LASTEXITCODE -ne 0){throw ('Compile failed: '+$test)}
}
& (Join-Path $output 'QueueTests.exe')
if($LASTEXITCODE -ne 0){throw 'Queue test failed'}
& (Join-Path $output 'LatencyBench.exe') verify
if($LASTEXITCODE -ne 0){throw 'Latency gate failed'}
if($BaselineExe){
 $baseline=Join-Path $output 'baseline'; New-Item -ItemType Directory -Force -Path $baseline | Out-Null
 Copy-Item -LiteralPath $BaselineExe -Destination (Join-Path $baseline 'ChaoChaoKvm.exe') -Force
 & $csc /nologo "/out:$baseline\CompatibilityProbe.exe" "/reference:$baseline\ChaoChaoKvm.exe" (Join-Path $ProjectRoot 'tests\CompatibilityProbe.cs')
 if($LASTEXITCODE -ne 0){throw 'Baseline probe compilation failed'}
 & (Join-Path $output 'CompatibilityProbe.exe') (Join-Path $baseline 'CompatibilityProbe.exe')
 if($LASTEXITCODE -ne 0){throw 'New client / old server failed'}
 & (Join-Path $baseline 'CompatibilityProbe.exe') (Join-Path $output 'CompatibilityProbe.exe')
 if($LASTEXITCODE -ne 0){throw 'Old client / new server failed'}
}
Write-Output 'LAN_LATENCY_CHECKS_PASS (synthetic localhost, no OS input or capture)'
