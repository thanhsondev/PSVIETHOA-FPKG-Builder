#!/usr/bin/env bash
# Chạy ứng dụng ở chế độ phát triển trên macOS/Linux.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(dirname "$(readlink -f "$(command -v dotnet)")")")}"
exec dotnet run --project "$ROOT/src/PsViethoa.FpkgBuilder.App/PsViethoa.FpkgBuilder.App.csproj" -c Debug "$@"
