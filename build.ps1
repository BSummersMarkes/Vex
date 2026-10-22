# Builds Vex.exe (single file, no install needed).
#   powershell -ExecutionPolicy Bypass -File .\build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# A running Vex locks the exe; ask it to leave first (his watchdog is a Vex.exe too, so it goes with him).
# It used to be left stopped, which looked to Ben like Vex vanishing; now the note below tells the next start
# why he was gone, and he's started again at the end, whether or not the build worked.
$running = @(Get-Process Vex -ErrorAction SilentlyContinue | ForEach-Object { $_.Path } | Where-Object { $_ } | Select-Object -Unique)
if ($running.Count -gt 0) {
    $data = Join-Path $env:LOCALAPPDATA 'Vex'
    if (Test-Path $data) { Set-Content (Join-Path $data 'stopped-by.txt') 'build.ps1' }
}
Get-Process Vex -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

try {
    dotnet publish "$root\src\Vex\Vex.csproj" -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=none `
        -o "$root\src\Vex\publish"
    if ($LASTEXITCODE -ne 0) { throw "Build failed" }

    Copy-Item "$root\src\Vex\publish\Vex.exe" "$root\Vex.exe" -Force
    $mb = [math]::Round((Get-Item "$root\Vex.exe").Length / 1MB, 1)
    Write-Host "Built $root\Vex.exe ($mb MB)"
}
finally {
    # Bring back whichever Vex was running (the new build, if it was the repo's Vex.exe).
    foreach ($exe in $running) { if (Test-Path $exe) { Start-Process $exe; Write-Host "Restarted $exe" } }
}
