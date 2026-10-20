$ErrorActionPreference = 'Stop'
# Rebuild only the generated package directory; prevent stale DLLs/nested licenses.
if (Test-Path -LiteralPath 'dist/MozChecker') { Remove-Item -LiteralPath 'dist/MozChecker' -Recurse -Force }
# Framework-dependent, ordinary WPF files. No Python, packer, single-file extraction, or trimming.
dotnet publish dotnet/MozChecker.Desktop --configuration Release --self-contained false --no-restore -o dist/MozChecker
if ($LASTEXITCODE -ne 0) { throw 'WPF publish failed' }
Copy-Item docs/WINDOWS-DOTNET.txt dist/MozChecker/README.txt
Copy-Item docs/licenses dist/MozChecker/licenses -Recurse
