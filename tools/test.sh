#!/usr/bin/env bash
#
# Runs the headless tests over Sim/. No game required, only its assemblies.
#
#     ./tools/test.sh                     # the suite
#     ./tools/test.sh Debug               # with the optimiser off, to step through one
#     ./tools/test.sh --filter Medium     # anything else goes to `dotnet test`
#
# Release by default because it is the configuration the shipped mod compiles Sim/ in; nothing under
# Sim/ is conditioned on DEBUG, so the two differ in speed and floating-point contraction only.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck source=env.sh
source "$REPO_ROOT/tools/env.sh"

# Only a leading Debug/Release is a configuration; everything else is `dotnet test`'s.
CONFIG=Release
case "${1:-}" in
    Debug|Release) CONFIG="$1"; shift ;;
esac

dotnet test "$REPO_ROOT/tests/KSAStructures.Tests/KSAStructures.Tests.csproj" -c "$CONFIG" --nologo "$@"
