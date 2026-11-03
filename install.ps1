# Builds Claude Buddy, copies it to %USERPROFILE%\ClaudeBuddy, adds it to autostart and launches it.
# Not AppData: packaged apps like Claude Desktop get a private copy of it that Windows cannot see at logon.
# Usage: powershell -ExecutionPolicy Bypass -File install.ps1 [-BuildOnly] [-NoLaunch]
param([switch]$BuildOnly, [switch]$NoLaunch)
$ErrorActionPreference = 'Stop'

$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$out = Join-Path $PSScriptRoot 'ClaudeBuddy.exe'
& $csc /nologo /target:winexe /codepage:65001 /optimize+ /warn:3 "/out:$out" "/lib:$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\WPF" /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:WindowsBase.dll /r:UIAutomationClient.dll /r:UIAutomationTypes.dll (Join-Path $PSScriptRoot 'Buddy.cs')
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
if ($BuildOnly) { return }

Get-Process ClaudeBuddy -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

$dir = Join-Path $env:USERPROFILE 'ClaudeBuddy'
New-Item -ItemType Directory -Force $dir | Out-Null
$exe = Join-Path $dir 'ClaudeBuddy.exe'
Copy-Item $out $exe -Force

$reg = Start-Process $exe -ArgumentList '--autostart' -Wait -PassThru
if ($reg.ExitCode -ne 0) { throw "Autostart registration failed: $($reg.ExitCode)" }

# Started through the task, so it runs the same way as at logon and outlives this terminal.
if (-not $NoLaunch) { schtasks /run /tn ClaudeBuddy | Out-Null }
"Installed: $exe"
