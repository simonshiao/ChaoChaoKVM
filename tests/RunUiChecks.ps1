param([string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot), [string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$refDir = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.6.2'
$source = Join-Path $ProjectRoot 'src\ChaoChaoKvm.Net472'
$testOutput = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $ProjectRoot 'tests\ui-build' }
New-Item -ItemType Directory -Force -Path $testOutput | Out-Null
& 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe' (Join-Path $source 'ChaoChaoKvm.Net472.csproj') /t:Rebuild /p:Configuration=Release /p:Platform=AnyCPU "/p:OutputPath=$testOutput\" "/p:IntermediateOutputPath=$testOutput\obj\" "/p:FrameworkPathOverride=$refDir" /verbosity:minimal
if ($LASTEXITCODE -ne 0) { throw 'UI build failed' }
& 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' /nologo /target:exe "/out:$testOutput\UiBehaviorTests.exe" "/reference:$testOutput\ChaoChaoKvm.exe" "/reference:$testOutput\Microsoft.Web.WebView2.Core.dll" "/reference:$refDir\WindowsBase.dll" "/reference:$refDir\PresentationCore.dll" "/reference:$refDir\PresentationFramework.dll" "/reference:$refDir\System.Xaml.dll" (Join-Path $ProjectRoot 'tests\UiBehaviorTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'UI test compilation failed' }
& (Join-Path $testOutput 'UiBehaviorTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Native UI behavior tests failed' }
node --check (Join-Path $source 'Web\app.js')
if ($LASTEXITCODE -ne 0) { throw 'JavaScript syntax failed' }
node (Join-Path $ProjectRoot 'tests\web-state-tests.cjs') (Join-Path $source 'Web')
if ($LASTEXITCODE -ne 0) { throw 'Web state tests failed' }
Write-Output 'KVM_UI_CHECKS_PASS (no network, no remote input, no browser visual acceptance)'
