#!/usr/bin/env bash
# One recovery implementation across platforms; PowerShell 7 and PostgreSQL clients required.
set -euo pipefail
umask 077
command -v pwsh >/dev/null || { echo 'PowerShell 7 is required; see the deployment runbook.' >&2; exit 1; }
exec pwsh -NoProfile -File "$(dirname "$0")/backup-db.ps1" "$@"
