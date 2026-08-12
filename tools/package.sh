#!/usr/bin/env bash
# Build the Jellyfin-installable zip: the two artifacts named in build.yaml
# plus the meta.json the server reads at load time. Output: dist/.
set -euo pipefail

repo="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${1:-$repo/dist}"
proj="$repo/Jellyfin.Plugin.KofinSyncQueue/Jellyfin.Plugin.KofinSyncQueue.csproj"
stage="$repo/artifacts/publish"

version="$(sed -n 's/.*<AssemblyVersion>\(.*\)<\/AssemblyVersion>.*/\1/p' "$proj")"
guid="$(sed -n 's/^guid: *"\(.*\)"/\1/p' "$repo/build.yaml")"
name="$(sed -n 's/^name: *"\(.*\)"/\1/p' "$repo/build.yaml")"
target_abi="$(sed -n 's/^targetAbi: *"\(.*\)"/\1/p' "$repo/build.yaml")"
owner="$(sed -n 's/^owner: *"\(.*\)"/\1/p' "$repo/build.yaml")"
overview="$(sed -n 's/^overview: *"\(.*\)"/\1/p' "$repo/build.yaml")"
description="$(sed -n 's/^description: *"\(.*\)"/\1/p' "$repo/build.yaml")"

# The changelog block, flattened to one JSON string. repository.kontell's
# generate_jellyfin_repo.py already reads meta.json's "changelog" and has been
# getting "" for it, which is what an empty release note in the Jellyfin plugin
# catalogue was: a field nobody wrote, not a field nobody wanted.

mkdir -p "$(dirname "$out")"  # Create output directory if it doesn't exist

changelog="$(awk '
    /^changelog:[[:space:]]*\|-?[[:space:]]*$/ { flag = 1; next }
    flag && /^[^[:space:]]/ { exit }
    flag { sub(/^  /, ""); print }
' "$repo/build.yaml" \
    | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' \
    | awk 'NR > 1 { printf "\\n" } { printf "%s", $0 }')"

dotnet publish "$proj" -c Release -o "$stage"

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

cp "$stage/Jellyfin.Plugin.KofinSyncQueue.dll" "$stage/LiteDB.dll" "$work/"

cat > "$work/meta.json" <<EOF
{
    "category": "General",
    "guid": "$guid",
    "name": "$name",
    "overview": "$overview",
    "description": "$description",
    "owner": "$owner",
    "targetAbi": "$target_abi",
    "timestamp": "$(date -u +%Y-%m-%dT%H:%M:%SZ)",
    "version": "$version",
    "changelog": "$changelog",
    "status": "Active",
    "autoUpdate": false
}
EOF

mkdir -p "$out"
zip_path="$out/kofin-sync-queue_$version.zip"
rm -f "$zip_path"
(cd "$work" && zip -q -r "$zip_path" .)

echo "$zip_path"
unzip -l "$zip_path"
