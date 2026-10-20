# Click accuracy check on a real desktop.
# Opens a test window that logs every click in client pixels, plays macro.json against it,
# and compares what the window received with what the macro asked for.
#
#   dotnet build tools/click-check/TargetApp -c Release
#   dotnet build UITestAutomation.sln -c Release
#   powershell -ExecutionPolicy Bypass -File tools/click-check/run.ps1
#
# The screen must be unlocked. Windows drops synthetic input on a locked desktop.
# Do not touch the mouse for about five seconds while it runs.
param(
    [string]$Exe = "$PSScriptRoot\..\..\UITestAutomation\bin\Release\net8.0-windows\UITestAutomation.exe",
    [string]$Target = "$PSScriptRoot\TargetApp\bin\Release\net8.0-windows\TargetApp.exe"
)
$ErrorActionPreference = "Stop"
$log = Join-Path $env:TEMP ("uta-click-check-{0}.log" -f (Get-Date -Format yyyyMMddHHmmss))
$expected = (Get-Content "$PSScriptRoot\macro.json" -Raw | ConvertFrom-Json).Events | ForEach-Object { "$($_.X),$($_.Y)" }

Add-Type @"
using System; using System.Runtime.InteropServices;
public static class ClickCheck {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
}
"@

$tp = Start-Process $Target -ArgumentList "`"$log`"" -PassThru
$h = [IntPtr]::Zero
# PowerShell turns $null into "" for string parameters; FindWindow needs a real null class name.
for ($i = 0; $i -lt 120 -and $h -eq [IntPtr]::Zero; $i++) { Start-Sleep -Milliseconds 250; $h = [ClickCheck]::FindWindow([NullString]::Value, "UTA Target Window") }
if ($h -eq [IntPtr]::Zero) { Write-Output "FAIL: test window did not open"; exit 2 }

$app = Start-Process $Exe -ArgumentList "`"$PSScriptRoot\macro.json`"" -PassThru
try {
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $A = [System.Windows.Automation.AutomationElement]; $T = [System.Windows.Automation.TreeScope]
    $byName = { param($n) New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $n) }
    $win = $null; $play = $null
    for ($i = 0; $i -lt 120 -and -not $play; $i++) {
        Start-Sleep -Milliseconds 250
        if (-not $win) { $win = $A::RootElement.FindFirst($T::Children, (& $byName "UI Test Automation")) }
        if ($win) { $play = $win.FindFirst($T::Descendants, (& $byName "Play")) }
    }
    if (-not $play) { Write-Output "FAIL: Play button not found"; exit 2 }

    $play.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Seconds 5

    $got = @(Get-Content $log | Where-Object { $_ -ne "start" })
    Write-Output ("expected: " + ($expected -join "  "))
    Write-Output ("received: " + ($got -join "  "))
    if (($got -join "|") -eq ($expected -join "|")) { Write-Output "PASS"; exit 0 }
    Write-Output "FAIL"; exit 1
}
finally {
    Stop-Process -Id $app.Id, $tp.Id -ErrorAction SilentlyContinue
}
