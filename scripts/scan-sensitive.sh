#!/usr/bin/env bash
set -euo pipefail

if (($# == 0)); then
  printf 'Usage: %s <file-or-directory> [...]\n' "$0" >&2
  exit 2
fi

patterns=(
  'eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{8,}'
  '(?i)Bearer[[:space:]]+[A-Za-z0-9._~+/=-]{20,}'
  '(?i)(?<![A-Za-z0-9_-])(sk|ak)-[A-Za-z0-9_-]{16,}(?![A-Za-z0-9_-])'
  '(?i)(auth_token|access_token|refresh_token|session|sessionid)[[:space:]]*=[[:space:]]*[A-Za-z0-9%._~+/=-]{20,}'
  '(?i)(password|passwd|access[_-]?token|refresh[_-]?token|api[_-]?key|cookie)[[:space:]]*[:=][[:space:]]*["'"'][^"'"']{8,}["'"']'
  '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----'
)
local_path_pattern='(?i)([A-Z]:[\\/]Users[\\/][^\\/:*?"<>|[:space:]]+|/(root|home/[A-Za-z0-9._-]+)/)'
email_pattern='(?i)[A-Z0-9._%+-]+@([A-Z0-9-]+\.)+[A-Z]{2,}'
findings=0
scanned=0
scratch="$(mktemp)"
trap 'rm -f "$scratch"' EXIT

scan_file() {
  local file="$1"
  local mime_type
  local pattern
  local email

  mime_type="$(file -b --mime-type -- "$file")"
  strings -a -n 4 -- "$file" >"$scratch"
  scanned=$((scanned + 1))
  for pattern in "${patterns[@]}"; do
    if rg --pcre2 -q -- "$pattern" "$scratch"; then
      printf '[SensitivePattern] %s\n' "$file" >&2
      findings=$((findings + 1))
      break
    fi
  done

  case "$mime_type" in
    text/*|application/json|application/xml|application/x-empty)
      if rg --pcre2 -q -- "$local_path_pattern" "$scratch"; then
        printf '[SensitivePattern] %s\n' "$file" >&2
        findings=$((findings + 1))
      fi
      while IFS= read -r email; do
        case "${email,,}" in
          *@example.com|*@*.test|*@*.invalid) ;;
          *)
            printf '[EmailAddress] %s\n' "$file" >&2
            findings=$((findings + 1))
            break
            ;;
        esac
      done < <(rg --pcre2 -o --no-line-number -- "$email_pattern" "$scratch" || true)
      ;;
  esac
}

for target in "$@"; do
  if [[ -d "$target" ]]; then
    while IFS= read -r -d '' file; do
      scan_file "$file"
    done < <(find "$target" -type f -not -path '*/.playwright/*' -print0)
  elif [[ -f "$target" ]]; then
    scan_file "$target"
  else
    printf 'Scan target not found: %s\n' "$target" >&2
    exit 2
  fi
done

if ((findings > 0)); then
  printf 'Sensitive information scan failed: %d finding(s).\n' "$findings" >&2
  exit 1
fi

printf 'Sensitive information scan clean (%d files).\n' "$scanned"
