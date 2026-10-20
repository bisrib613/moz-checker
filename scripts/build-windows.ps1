$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
uv sync --frozen --extra dev
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
uv run --frozen --extra dev python -m PyInstaller --noconfirm --clean --windowed --onedir --name MozChecker --paths src --hidden-import keyring.backends.Windows --collect-data tldextract scripts/desktop_entry.py
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Write-Host "Portable app: dist/MozChecker/MozChecker.exe. Keep the whole MozChecker folder."
