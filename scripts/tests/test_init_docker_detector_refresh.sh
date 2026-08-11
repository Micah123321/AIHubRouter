#!/usr/bin/env bash
set -euo pipefail

readonly test_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly project_root="$(cd "$test_directory/../.." && pwd)"
readonly init_script="$project_root/scripts/init-docker.sh"
readonly dockerignore="$project_root/.dockerignore"
readonly test_root="$(mktemp -d)"

cleanup_test_root() {
  rm -rf "$test_root"
}

trap cleanup_test_root EXIT

die() {
  printf '测试失败：%s\n' "$*" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || die "缺少命令：$1"
}

assert_contains() {
  local file_path="$1"
  local expected="$2"

  if ! grep -Fq -- "$expected" "$file_path"; then
    printf '断言失败：%s 未包含：%s\n' "$file_path" "$expected" >&2
    exit 1
  fi
}

assert_before() {
  local file_path="$1"
  local first="$2"
  local second="$3"
  local first_line
  local second_line

  first_line="$(grep -n -F -- "$first" "$file_path" | sed -n '1s/:.*//p')"
  second_line="$(grep -n -F -- "$second" "$file_path" | sed -n '1s/:.*//p')"
  if (( first_line >= second_line )); then
    printf '断言失败：%s 中顺序错误：%s 应在 %s 之前。\n' "$file_path" "$first" "$second" >&2
    exit 1
  fi
}

[[ -f "$init_script" ]] || { printf '缺少初始化脚本：%s\n' "$init_script" >&2; exit 1; }
[[ -f "$dockerignore" ]] || { printf '缺少 Docker 忽略文件：%s\n' "$dockerignore" >&2; exit 1; }

bash -n "$init_script"

assert_contains "$init_script" 'readonly detector_revision="cc9c53c43c83da8d52220b5da2e2c94d7ca4d9cf"'
assert_contains "$init_script" 'readonly detector_version="4.1.0"'
assert_contains "$init_script" 'git clone --quiet --no-tags --no-checkout'
assert_contains "$init_script" 'git -C "$temporary_detector" checkout --quiet --detach "$detector_revision"'
assert_contains "$init_script" 'temporary_detector/gpt56_vnext/detector.py'
assert_contains "$init_script" 'temporary_detector/$detector_baseline'
assert_contains "$init_script" 'temporary_detector_version="$(tr -d '\''\r\n'\'' < "$temporary_detector/VERSION")"'
assert_contains "$init_script" 'backup_base="$repo_root/gpt56_api_detector.backup.$(date +%Y%m%d%H%M%S)"'
assert_contains "$init_script" 'mv -- "$detector_directory" "$detector_backup_directory"'
assert_contains "$init_script" 'mv -- "$temporary_detector" "$detector_directory"'
assert_contains "$init_script" 'mv -- "$backup_detector" "$detector_directory"'
assert_before "$init_script" \
  'temporary_detector_version="$(tr -d' \
  'mv -- "$detector_directory" "$detector_backup_directory"'
assert_before "$init_script" \
  'mv -- "$detector_directory" "$detector_backup_directory"' \
  'mv -- "$temporary_detector" "$detector_directory"'

assert_contains "$dockerignore" 'gpt56_api_detector.backup.*'
assert_contains "$dockerignore" '.gpt56_api_detector.*'

readonly function_file="$test_root/ensure_detector_source.sh"
awk '
  /^detector_source_is_current\(\) \{/ { capturing = 1 }
  /^write_environment_file\(\) \{/ { exit }
  capturing { print }
' "$init_script" > "$function_file"

prepare_fixture() {
  local case_root="$1"
  local fixture_root="$case_root/fixture"
  local detector_root="$case_root/repo/gpt56_api_detector"

  mkdir -p "$fixture_root/gpt56_vnext/baselines" "$detector_root"
  printf 'legacy detector\n' > "$detector_root/old-marker.txt"
  printf '# detector fixture\n' > "$fixture_root/gpt56_vnext/detector.py"
  printf '# presets fixture\n' > "$fixture_root/gpt56_vnext/presets.py"
  printf '4.1.0\n' > "$fixture_root/VERSION"
  printf '{}\n' > "$fixture_root/gpt56_vnext/baselines/trusted_fingerprint_v3.json"
}

run_refresh_case() {
  local case_root="$1"
  local expect_success="$2"
  local fake_bin="$case_root/bin"
  local case_repo_root="$case_root/repo"
  local case_detector_directory="$case_repo_root/gpt56_api_detector"
  local case_fixture="$case_root/fixture"
  local fake_git="$fake_bin/git"

  prepare_fixture "$case_root"
  mkdir -p "$fake_bin"
  printf '%s\n' \
    '#!/usr/bin/env bash' \
    'set -euo pipefail' \
    'if [[ "$1" == "clone" ]]; then' \
    '  [[ "${FAKE_GIT_FAIL_CLONE:-0}" != "1" ]] || exit 1' \
    '  destination="${@: -1}"' \
    '  mkdir -p "$destination"' \
    '  cp -R "$FAKE_GIT_FIXTURE/." "$destination/"' \
    '  exit 0' \
    'fi' \
    'if [[ "$1" == "-C" ]]; then' \
    '  [[ "${FAKE_GIT_FAIL_CHECKOUT:-0}" != "1" ]] || exit 1' \
    '  exit 0' \
    'fi' \
    'exit 1' > "$fake_git"
  chmod +x "$fake_git"

  (
    local repo_root="$case_repo_root"
    local detector_directory="$case_detector_directory"
    local detector_repository='test://gpt56_api_detector'
    local detector_revision='test-revision'
    local detector_version='4.1.0'
    local detector_baseline='gpt56_vnext/baselines/trusted_fingerprint_v3.json'
    local temporary_detector_parent=''

    export PATH="$fake_bin:$PATH"
    export FAKE_GIT_FIXTURE="$case_fixture"
    export FAKE_GIT_FAIL_CHECKOUT="$((1 - expect_success))"
    source "$function_file"
    ensure_detector_source

    if [[ "$expect_success" == "1" ]]; then
      [[ -f "$detector_directory/gpt56_vnext/detector.py" ]] || exit 1
      [[ "$(tr -d '\r\n' < "$detector_directory/VERSION")" == '4.1.0' ]] || exit 1
      shopt -s nullglob
      local backups=("$repo_root"/gpt56_api_detector.backup.*)
      [[ "${#backups[@]}" -eq 1 ]] || exit 1
      [[ -f "${backups[0]}/old-marker.txt" ]] || exit 1

      export FAKE_GIT_FAIL_CLONE=1
      ensure_detector_source
    fi
  )
}

success_case="$test_root/success"
run_refresh_case "$success_case" 1

failure_case="$test_root/failure"
if run_refresh_case "$failure_case" 0; then
  printf '断言失败：checkout 失败场景应返回非零状态。\n' >&2
  exit 1
fi
[[ -f "$failure_case/repo/gpt56_api_detector/old-marker.txt" ]] ||
  { printf '断言失败：checkout 失败后旧检测器目录未保留。\n' >&2; exit 1; }

printf '检测器一键刷新契约自检通过。\n'
