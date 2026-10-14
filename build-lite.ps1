# Builds "Vex Lite.exe": Vex without anything that needs Claude, for sharing (see src\VexLite\README-LITE.md).
# .NET Framework 4.8 is part of Windows 10/11, so it's one small file with nothing to install.
#   powershell -ExecutionPolicy Bypass -File .\build-lite.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# A running copy locks the exe; ask it to leave first (only the lite one, never the full Vex).
Get-Process VexLite -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

dotnet build "$root\src\VexLite\VexLite.csproj" -c Release -p:DebugType=none -o "$root\src\VexLite\publish"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

Copy-Item "$root\src\VexLite\publish\VexLite.exe" "$root\Vex Lite.exe" -Force
$kb = [math]::Round((Get-Item "$root\Vex Lite.exe").Length / 1KB)
Write-Host "Built $root\Vex Lite.exe ($kb KB)"
