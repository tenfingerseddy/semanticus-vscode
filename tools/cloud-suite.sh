#!/usr/bin/env bash
# Fresh cloud checkout, or resume against the same persistent output folder.
set -euo pipefail

if [[ $# != 3 ]]; then
  printf 'Usage: tools/cloud-suite.sh <branch> <all|0,1,...> <out>\n' >&2
  exit 2
fi
branch=$1
selection=$2
mkdir -p -- "$3"
out=$(cd -- "$3" && pwd)
repo=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
cd -- "$repo"

unset JAVA_TOOL_OPTIONS
export DOTNET_CLI_UI_LANGUAGE=en
export DOTNET_PROCESSOR_COUNT=2
export MSBUILDDISABLENODEREUSE=1
export TMPDIR="${TMPDIR:-$out/t}"
mkdir -p -- "$TMPDIR"
if [[ -d /opt/dotnet8 ]]; then
  export DOTNET_ROOT=/opt/dotnet8
  export PATH="$DOTNET_ROOT:$PATH"
fi

# Cloud images do not need the laptop's shared lock. Use it when the image provides it.
runner=(nice -n 10)
if command -v semlock >/dev/null 2>&1; then runner=(semlock nice -n 10); fi

# A cached cloud container can hold an old local copy of the branch (27 Oct run 3 shard 3 built a48b6e48, not the
# tip, the plan check refused it, and the session sat idle). Always take the branch fresh from origin.
git fetch --quiet origin "$branch"
git switch --force-create "$branch" "origin/$branch"
git submodule update --init --recursive
"${runner[@]}" dotnet build Semanticus.sln -c Release -m:2 -nodeReuse:false -p:UseSharedCompilation=false
if [[ ! -f "$out/shards.json" ]]; then
  "${runner[@]}" node tools/suite-shard.mjs plan --out "$out"
fi

# Derive "all" from the plan: a long filter can make more than eight shards.
# Reject duplicate indices before starting a shard.
indices=$(node --input-type=module - "$out/shards.json" "$selection" <<'NODE'
import { readFileSync } from 'node:fs';
const plan = JSON.parse(readFileSync(process.argv[2], 'utf8'));
const selection = process.argv[3];
const ids = selection === 'all' ? plan.shards.map((s) => s.id) : selection.split(/[,\s]+/).filter(Boolean);
if (!ids.length || ids.some((id) => !/^\d+$/.test(String(id)) || !plan.shards.some((s) => s.id === Number(id)))
    || new Set(ids.map(Number)).size !== ids.length) {
  console.error('Use all or distinct zero-based shard indices from shards.json.');
  process.exit(2);
}
console.log(ids.join('\n'));
NODE
)

failed=0
while IFS= read -r shard; do
  if ! "${runner[@]}" node tools/suite-shard.mjs run --shard "$shard" --out "$out"; then failed=1; fi
done <<< "$indices"
if ! node tools/suite-shard.mjs merge --out "$out"; then failed=1; fi
exit "$failed"
