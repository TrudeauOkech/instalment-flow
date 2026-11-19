#!/usr/bin/env bash
# Records new public API in each project's PublicAPI.Unshipped.txt.
# Builds the solution, collects RS0016 ("not part of the declared public API") diagnostics and appends the
# reported symbols to the owning project's file. Review the diff: every line is a public API commitment.
set -euo pipefail
cd "$(dirname "$0")/.."

log=$(mktemp)
trap 'rm -f "$log"' EXIT
dotnet build -c Release -v q -p:TreatWarningsAsErrors=false "$@" >"$log" 2>&1 || true

grep -E "(error|warning) RS0016:" "$log" \
  | sed -E "s/^(.*\.cs)\([0-9]+,[0-9]+\): (error|warning) RS0016: Symbol '(.*)' is not part of the declared public API.* \[(.*\.csproj)\]$/\4\t\3/" \
  | sort -u \
  | while IFS=$'\t' read -r project symbol; do
      printf '%s\n' "$symbol" >>"$(dirname "$project")/PublicAPI.Unshipped.txt"
    done

for file in src/*/PublicAPI.Unshipped.txt; do
  { head -n1 "$file"; tail -n +2 "$file" | sort -u; } >"$file.tmp" && mv "$file.tmp" "$file"
done

remaining=$(dotnet build -c Release -v q "$@" 2>&1 | grep -c "error RS0016" || true)
echo "Public API files updated; remaining RS0016 errors: ${remaining}"
