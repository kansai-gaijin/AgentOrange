# Runs the headless checks in this folder against the dev build (dotnet build client first), each in
# a data folder of its own under the output folder, and says which passed. A check named
# "<name>-again.txt" runs after "<name>.txt" in the same data folder, as the app started again.
#
#   ./tools/checks/run.ps1                 every check
#   ./tools/checks/run.ps1 milestones      one, with its -again check
#
# Each check's log and screenshots are in <output>/<name>/out.
param(
    [string]$Only = "",
    [string]$Output = (Join-Path ([IO.Path]::GetTempPath()) "AgentOrange-Checks")
)

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$app = Join-Path $root "client\bin\Debug\net10.0-windows\AgentOrange.exe"
if (-not (Test-Path $app)) { throw "Build the dev app first: dotnet build client" }

$failed = 0
$checks = Get-ChildItem $PSScriptRoot -Filter *.txt | Where-Object { $_.BaseName -notlike "*-again" -and ($Only -eq "" -or $_.BaseName -eq $Only) }
foreach ($check in $checks) {
    $folder = Join-Path $Output $check.BaseName
    if (Test-Path $folder) { Remove-Item -Recurse -Force $folder }
    $scripts = @($check.FullName)
    $again = Join-Path $PSScriptRoot "$($check.BaseName)-again.txt"
    if (Test-Path $again) { $scripts += $again }

    $passed = $true
    foreach ($script in $scripts) {
        $out = Join-Path $folder ("out" + $(if ($script -eq $again) { "-again" } else { "" }))
        $run = Start-Process -FilePath $app -ArgumentList "--headless", "`"$(Join-Path $folder 'data')`"", "`"$script`"", "`"$out`"" -Wait -PassThru
        $log = Get-Content (Join-Path $out "headless.log") -ErrorAction SilentlyContinue
        if ($run.ExitCode -ne 0) {
            $passed = $false
            Write-Host "FAILED $([IO.Path]::GetFileName($script))" -ForegroundColor Red
            $log | Select-Object -Last 3 | ForEach-Object { Write-Host "  $_" }
            break
        }
        $log | Where-Object { $_ -like "*settled in*" } | ForEach-Object { Write-Host "  $_" }
    }
    if ($passed) { Write-Host "passed $($check.BaseName)" -ForegroundColor Green } else { $failed++ }
}
Write-Host "Logs and screenshots: $Output"
exit $failed
