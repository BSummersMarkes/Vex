# Builds Vex.exe (single file, no install needed).
#   powershell -ExecutionPolicy Bypass -File .\build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# A running Vex locks the exe; ask it to leave first.
Get-Process Vex -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

dotnet publish "$root\src\Vex\Vex.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none `
    -o "$root\src\Vex\publish"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

Copy-Item "$root\src\Vex\publish\Vex.exe" "$root\Vex.exe" -Force
$mb = [math]::Round((Get-Item "$root\Vex.exe").Length / 1MB, 1)
Write-Host "Built $root\Vex.exe ($mb MB)"
