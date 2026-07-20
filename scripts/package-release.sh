#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
artifacts_root="$repo_root/artifacts"
packages_root="$artifacts_root/packages"
staging_root="$artifacts_root/package-staging"

if (($# > 0)); then
  runtimes=("$@")
else
  runtimes=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)
fi

"$repo_root/scripts/publish.sh" "${runtimes[@]}"
rm -rf "$staging_root"
mkdir -p "$staging_root" "$packages_root"
rm -f "$packages_root/SHA256SUMS.txt"

for rid in "${runtimes[@]}"; do
  package_name="AIHubRouter-$rid"
  package_root="$staging_root/$package_name"
  archive="$packages_root/$package_name.zip"

  rm -rf "$package_root"
  rm -f "$archive"
  mkdir -p "$package_root"
  cp -a "$artifacts_root/$rid/cli" "$package_root/cli"
  cp -a "$artifacts_root/$rid/desktop" "$package_root/desktop"
  cp "$repo_root/README.md" "$package_root/README.md"
  find "$package_root" -type f -name '*.pdb' -delete

  "$repo_root/scripts/scan-sensitive.sh" "$package_root"
  (
    cd "$staging_root"
    zip -q -9 -r "$archive" "$package_name"
  )
  unzip -tqq "$archive"
  if unzip -Z1 "$archive" | rg -q '(^/|(^|/)\.\.(/|$))'; then
    printf 'Unsafe archive path detected: %s\n' "$archive" >&2
    exit 1
  fi
  verification_root="$staging_root/verify-$rid"
  rm -rf "$verification_root"
  mkdir -p "$verification_root"
  unzip -q "$archive" -d "$verification_root"
  "$repo_root/scripts/scan-sensitive.sh" "$verification_root"
  rm -rf "$verification_root"
  (
    cd "$packages_root"
    sha256sum "$package_name.zip"
  ) >>"$packages_root/SHA256SUMS.txt"
  printf 'Packaged %s\n' "$archive"
done

rm -rf "$staging_root"
printf 'Checksums: %s\n' "$packages_root/SHA256SUMS.txt"
