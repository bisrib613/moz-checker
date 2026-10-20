#!/usr/bin/env bash
set -euo pipefail
cd /workspace/moz-checker
export UV_CACHE_DIR=/workspace/.cache/uv
export XDG_CACHE_HOME=/workspace/.cache
mkdir -p "$UV_CACHE_DIR" "$XDG_CACHE_HOME"
uv sync --frozen --extra dev
.venv/bin/ruff check src tests
QT_QPA_PLATFORM=offscreen .venv/bin/pytest -q
QT_QPA_PLATFORM=offscreen .venv/bin/python -m moz_checker --smoke
