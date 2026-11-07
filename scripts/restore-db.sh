#!/usr/bin/env bash
set -euo pipefail
umask 077
command -v pwsh >/dev/null || { echo 'PowerShell 7 is required; see the deployment runbook.' >&2; exit 1; }
exec pwsh -NoProfile -File "$(dirname "$0")/restore-db.ps1" "$@"
