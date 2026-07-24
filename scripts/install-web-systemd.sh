#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
  printf 'Run this installer as root.\n' >&2
  exit 1
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
binary="${1:-$repo_root/artifacts/linux-x64/web/aihub-router-web}"

if [[ ! -x "$binary" ]]; then
  printf 'Web binary not found or not executable: %s\n' "$binary" >&2
  exit 1
fi

if ! command -v openssl >/dev/null; then
  printf 'openssl is required to generate the HTTPS certificate.\n' >&2
  exit 1
fi

if ! getent passwd aihub-router-web >/dev/null; then
  useradd --system --home-dir /var/lib/aihub-router-web --shell /usr/sbin/nologin aihub-router-web
fi

install -d -m 0755 /opt/aihub-router-web
install -d -o aihub-router-web -g aihub-router-web -m 0700 /var/lib/aihub-router-web
install -d -o root -g aihub-router-web -m 0750 /etc/aihub-router-web
if systemctl is-active --quiet aihub-router-web.service 2>/dev/null; then
  systemctl stop aihub-router-web.service
fi
binary_directory="$(cd "$(dirname "$binary")" && pwd)"
cp -a "$binary_directory/." /opt/aihub-router-web/
install -m 0755 "$binary" /opt/aihub-router-web/aihub-router-web

environment_file=/etc/aihub-router-web/environment
certificate_file=/etc/aihub-router-web/aihub-router-web.pfx
if [[ ! -f "$environment_file" ]]; then
  master_key="$(openssl rand -base64 32 | tr -d '\n')"
  web_password="$(openssl rand -base64 24 | tr -d '\n')"
  certificate_password="$(openssl rand -base64 24 | tr -d '\n')"
  umask 077
  {
    printf 'AIHUB_ROUTER_MASTER_KEY=%s\n' "$master_key"
    printf 'AIHUB_WEB_PASSWORD=%s\n' "$web_password"
    printf 'Kestrel__Certificates__Default__Password=%s\n' "$certificate_password"
  } >"$environment_file"
  unset master_key certificate_password
  printf 'Generated Web access password: %s\n' "$web_password"
  unset web_password
fi
chmod 0600 "$environment_file"

if [[ ! -f "$certificate_file" ]]; then
  certificate_password="$(sed -n 's/^Kestrel__Certificates__Default__Password=//p' "$environment_file")"
  temporary_directory="$(mktemp -d)"
  trap 'rm -rf "$temporary_directory"' EXIT
  openssl req -x509 -newkey rsa:3072 -sha256 -nodes -days 825 \
    -keyout "$temporary_directory/key.pem" \
    -out "$temporary_directory/cert.pem" \
    -subj '/CN=AIHub Router Web' \
    -addext 'subjectAltName=DNS:localhost,IP:127.0.0.1'
  openssl pkcs12 -export \
    -out "$certificate_file" \
    -inkey "$temporary_directory/key.pem" \
    -in "$temporary_directory/cert.pem" \
    -passout "pass:$certificate_password"
  chmod 0640 "$certificate_file"
  chown root:aihub-router-web "$certificate_file"
  unset certificate_password
fi

install -m 0644 "$repo_root/deploy/systemd/aihub-router-web.service" /etc/systemd/system/
systemctl daemon-reload
systemctl enable aihub-router-web.service
systemctl restart aihub-router-web.service

printf 'AIHubRouter Web is listening on https://0.0.0.0:5443.\n'
printf 'Read the current access password with: sudo sed -n "s/^AIHUB_WEB_PASSWORD=//p" %s\n' "$environment_file"
