#!/usr/bin/env bash
# Moves reviewed public API from PublicAPI.Unshipped.txt to PublicAPI.Shipped.txt, freezing it.
# After this, removing or changing a shipped symbol is a build error (RS0017) and needs a major version.
# Usage: eng/ship-public-api.sh <project>...   (for example InstalmentFlow InstalmentFlow.Payments)
set -euo pipefail
cd "$(dirname "$0")/.."

if [ "$#" -eq 0 ]; then
  echo "Name the projects to ship, for example: $0 InstalmentFlow InstalmentFlow.Payments" >&2
  exit 2
fi

for project in "$@"; do
  dir="src/$project"
  unshipped="$dir/PublicAPI.Unshipped.txt"
  shipped="$dir/PublicAPI.Shipped.txt"
  [ -f "$unshipped" ] || { echo "$unshipped not found" >&2; exit 1; }

  before=$(grep -vc -e '^#nullable' -e '^$' "$shipped" || true)
  count=$(grep -vc -e '^#nullable' -e '^$' "$unshipped" || true)
  {
    echo "#nullable enable"
    cat "$shipped" "$unshipped" | grep -v -e '^#nullable' -e '^$' | LC_ALL=C sort -u
  } >"$shipped.tmp"
  after=$(grep -vc -e '^#nullable' -e '^$' "$shipped.tmp" || true)
  if [ "$after" -ne $((before + count)) ]; then
    echo "$project: expected $((before + count)) shipped entries, got $after (duplicates?)" >&2
    rm -f "$shipped.tmp"; exit 1
  fi
  mv "$shipped.tmp" "$shipped"
  echo "#nullable enable" >"$unshipped"
  echo "$project: shipped $count entries"
done
