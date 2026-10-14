# Builds Vex for macOS as Vex-mac.zip (Vex.app inside). See src\VexMac\README-MAC.md for getting it onto the Mac.
# Intel (osx-x64) code, which Apple Silicon Macs run through Rosetta: Apple requires a code signature on Apple
# Silicon code, which can't be made from Windows; Intel code doesn't need one. Needs Python (and Pillow for the icon).
#   powershell -ExecutionPolicy Bypass -File .\build-mac.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = "$root\src\VexMac\publish-osx"

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish "$root\src\VexMac\VexMac.csproj" -c Release -r osx-x64 --self-contained true -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

python "$root\src\VexMac\pack.py" $out "$root\Vex-mac.zip"
if ($LASTEXITCODE -ne 0) { throw "Packing failed" }
