#!/usr/bin/env bash
# Extrae la sección de una versión del CHANGELOG.md, para usarla como cuerpo de un GitHub Release.
# Uso: scripts/extract-changelog.sh 0.8.0 [CHANGELOG.md]
set -euo pipefail

VERSION="${1:?uso: extract-changelog.sh <version> [archivo]}"
FILE="${2:-CHANGELOG.md}"
HEADER="## [$VERSION]"

awk -v header="$HEADER" '
  index($0, header) == 1 { found=1; next }
  found && index($0, "## [") == 1 { exit }
  found && $0 == "---" { next }
  found { print }
' "$FILE" | sed -e '/./,$!d' -e ':a' -e '/^\n*$/{$d;N;ba' -e '}'
