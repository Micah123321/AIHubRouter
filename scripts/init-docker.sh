#!/usr/bin/env bash
set -euo pipefail

readonly script_directory="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly repo_root="$(cd "$script_directory/.." && pwd)"
readonly container_name="aihub-router-web"
readonly image_name="aihub-router-web:local"
readonly volume_name="aihub-router-web-data"
readonly environment_file="/etc/aihub-router-web.env"
readonly detector_directory="$repo_root/gpt56_api_detector"
readonly detector_repository="https://github.com/Micah123321/gpt56_api_detector.git"
readonly detector_revision="e9ef5d0f9cd4b0fa401a4e9960d959557610b852"

generated_password=""
temporary_file=""
temporary_detector_parent=""

die() {
  printf '错误：%s\n' "$*" >&2
  exit 1
}

require_command() {
  command -v "$1" >/dev/null 2>&1 || die "缺少命令：$1"
}

cleanup() {
  if [[ -n "$temporary_file" && -e "$temporary_file" ]]; then
    rm -f "$temporary_file"
  fi
  if [[ -n "$temporary_detector_parent" && -e "$temporary_detector_parent" ]]; then
    rm -rf "$temporary_detector_parent"
  fi
}

trap cleanup EXIT

if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
  die '请使用 root 执行：bash scripts/init-docker.sh'
fi

for command_name in docker openssl curl; do
  require_command "$command_name"
done

docker info >/dev/null 2>&1 || die 'Docker daemon 不可用，请先启动 Docker。'
[[ -f "$repo_root/Dockerfile" ]] || die "未找到 Dockerfile：$repo_root"

ensure_detector_source() {
  if [[ -f "$detector_directory/gpt56_vnext/detector.py" &&
    -f "$detector_directory/gpt56_vnext/presets.py" ]]; then
    return
  fi

  if [[ -e "$detector_directory" ]]; then
    die "参考检测器目录不完整：$detector_directory。请补齐 gpt56_vnext/detector.py 和 presets.py。"
  fi

  require_command git
  temporary_detector_parent="$(mktemp -d "$repo_root/.gpt56_api_detector.XXXXXX")"
  local temporary_detector="$temporary_detector_parent/source"

  printf '未找到参考检测器，正在获取固定版本：%s\n' "$detector_revision"
  if ! git clone --quiet --no-tags --no-checkout "$detector_repository" "$temporary_detector"; then
    die "无法获取参考检测器：$detector_repository。请检查服务器网络，或手动放置 $detector_directory。"
  fi
  if ! git -C "$temporary_detector" checkout --quiet --detach "$detector_revision"; then
    die "参考检测器不包含固定版本：$detector_revision。请手动放置匹配版本的 $detector_directory。"
  fi

  mv "$temporary_detector" "$detector_directory"
  rmdir "$temporary_detector_parent"
  temporary_detector_parent=""
}

write_environment_file() {
  local web_password="$1"
  local master_key="$2"

  temporary_file="$(mktemp /etc/aihub-router-web.env.tmp.XXXXXX)"

  umask 077
  {
    printf 'AIHUB_WEB_PASSWORD=%s\n' "$web_password"
    printf 'AIHUB_ROUTER_MASTER_KEY=%s\n' "$master_key"
    printf 'AIHUB_WEB_URLS=http://0.0.0.0:5080\n'
    printf 'AIHUB_WEB_ALLOW_HTTP=1\n'
  } > "$temporary_file"
  chmod 600 "$temporary_file"
  mv "$temporary_file" "$environment_file"
  temporary_file=""
}

validate_environment_file() {
  local variable_name

  [[ -r "$environment_file" ]] || die "无法读取环境文件：$environment_file"
  for variable_name in \
    AIHUB_WEB_PASSWORD \
    AIHUB_ROUTER_MASTER_KEY \
    AIHUB_WEB_URLS \
    AIHUB_WEB_ALLOW_HTTP; do
    grep -q "^${variable_name}=" "$environment_file" ||
      die "环境文件缺少变量 ${variable_name}，为避免覆盖密钥已停止：$environment_file"
  done

  grep -q '^AIHUB_WEB_URLS=http://0.0.0.0:5080$' "$environment_file" ||
    die '环境文件中的 AIHUB_WEB_URLS 不是 http://0.0.0.0:5080。请检查脚本管理的公网 HTTP 配置。'
  grep -q '^AIHUB_WEB_ALLOW_HTTP=1$' "$environment_file" ||
    die '环境文件中的 AIHUB_WEB_ALLOW_HTTP 不是 1。公网 HTTP 模式需要显式允许非回环 HTTP。'

  chmod 600 "$environment_file"
}

if [[ -e "$environment_file" ]]; then
  printf '发现已有环境文件，正在保留口令和主密钥并修复受管理配置。\n'
  existing_password="$(sed -n 's/^AIHUB_WEB_PASSWORD=//p' "$environment_file" | head -n 1)"
  existing_master_key="$(sed -n 's/^AIHUB_ROUTER_MASTER_KEY=//p' "$environment_file" | head -n 1)"
  [[ -n "$existing_password" ]] ||
    die "环境文件缺少 AIHUB_WEB_PASSWORD，为避免覆盖旧数据已停止：$environment_file"
  [[ -n "$existing_master_key" ]] ||
    die "环境文件缺少 AIHUB_ROUTER_MASTER_KEY，为避免覆盖旧数据已停止：$environment_file"
  write_environment_file "$existing_password" "$existing_master_key"
  unset existing_password existing_master_key
  validate_environment_file
else
  printf '首次运行，正在生成 Web 口令和凭据加密主密钥。\n'
  new_password="$(openssl rand -base64 24 | tr -d '\n')"
  new_master_key="$(openssl rand -base64 32 | tr -d '\n')"
  write_environment_file "$new_password" "$new_master_key"
  generated_password="$new_password"
  unset new_password new_master_key
fi

ensure_detector_source
printf '正在构建镜像：%s\n' "$image_name"
docker build --pull --tag "$image_name" "$repo_root"
printf '可靠性检测 worker 已随镜像部署：python3 + scripts/channel_detector_worker.py；检测密钥不会写入环境文件。\n'

if ! docker volume inspect "$volume_name" >/dev/null 2>&1; then
  printf '正在创建数据卷：%s\n' "$volume_name"
  docker volume create "$volume_name" >/dev/null
else
  printf '复用数据卷：%s\n' "$volume_name"
fi

if docker container inspect "$container_name" >/dev/null 2>&1; then
  printf '正在替换已有容器：%s（不会删除数据卷）\n' "$container_name"
  docker rm --force "$container_name" >/dev/null
fi

printf '正在启动容器：%s\n' "$container_name"
docker run --detach \
  --name "$container_name" \
  --restart unless-stopped \
  --env-file "$environment_file" \
  --mount "type=volume,src=$volume_name,dst=/app/data" \
  --publish 0.0.0.0:5080:5080 \
  "$image_name" >/dev/null

printf '正在等待 Web 健康检查。\n'
for ((attempt = 1; attempt <= 30; attempt++)); do
  if curl --fail --silent --show-error --max-time 2 \
    http://127.0.0.1:5080/healthz >/dev/null; then
    printf 'Web 容器已启动，健康检查通过。\n'
    if [[ -n "$generated_password" ]]; then
      printf '首次 Web 登录口令：%s\n' "$generated_password"
      printf '请立即保存该口令；后续运行不会再次显示。\n'
    fi
    printf '访问地址：http://服务器公网IP:5080\n'
    printf '安全警告：当前为明文 HTTP，登录口令和会话不会加密；请限制 5080 来源 IP，并尽快迁移到 HTTPS。\n' >&2
    printf '容器用户：%s\n' "$(docker inspect --format '{{.Config.User}}' "$container_name")"
    exit 0
  fi
  sleep 1
done

printf '错误：Web 健康检查超时，最近容器日志如下：\n' >&2
docker logs --tail 100 "$container_name" >&2 || true
exit 1
