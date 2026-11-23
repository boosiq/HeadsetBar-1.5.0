# Builds dist\HeadsetBarSetup.exe with the compiler that ships with Windows.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
New-Item -ItemType Directory -Force build, dist | Out-Null
function Invoke-Csc { & "$fw\csc.exe" /nologo /codepage:65001 /optimize @args; if ($LASTEXITCODE) { throw 'build failed' } }
$wpf = "/r:$fw\WPF\UIAutomationClient.dll", "/r:$fw\WPF\UIAutomationTypes.dll", "/r:$fw\WPF\WindowsBase.dll"
$app = 'src\Battery.cs', 'src\Hid.cs', 'src\Gauge.cs', 'src\Ui.cs', 'src\Flyout.cs', 'src\Widget.cs'
Invoke-Csc /target:exe /out:build\icon.exe src\Ui.cs src\MakeIcon.cs
& build\icon.exe build\app.ico; if ($LASTEXITCODE) { throw 'icon failed' }
Invoke-Csc /target:exe /out:build\test.exe src\Test.cs /main:Test @app @wpf
& build\test.exe; if ($LASTEXITCODE) { throw 'tests failed' }
Invoke-Csc /target:winexe /win32manifest:src\app.manifest /win32icon:build\app.ico /out:build\HeadsetBar.exe @app @wpf
Invoke-Csc /target:winexe /win32manifest:src\app.manifest /win32icon:build\app.ico /r:Microsoft.CSharp.dll "/resource:build\HeadsetBar.exe,HeadsetBar.exe" /out:dist\HeadsetBarSetup.exe src\Setup.cs src\Ui.cs