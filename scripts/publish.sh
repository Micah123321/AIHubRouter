#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
nuget_source="${AIHUB_NUGET_SOURCE:-https://mirrors.huaweicloud.com/repository/nuget/v3/index.json}"
nuget_fallback_source="${AIHUB_NUGET_FALLBACK_SOURCE:-https://api.nuget.org/v3/index.json}"
artifacts_root="$repo_root/artifacts"
solution="$repo_root/AIHubRouter.slnx"
cli_project="$repo_root/src/AIHubRouter.Cli/AIHubRouter.Cli.csproj"
desktop_project="$repo_root/src/AIHubRouter.Desktop/AIHubRouter.Desktop.csproj"
web_project="$repo_root/src/AIHubRouter.Web/AIHubRouter.Web.csproj"
test_project="$repo_root/tests/AIHubRouter.Core.Tests/AIHubRouter.Core.Tests.csproj"

if (($# > 0)); then
  runtimes=("$@")
else
  runtimes=(win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64)
fi

dotnet restore "$solution" \
  --configfile "$repo_root/NuGet.Config" \
  --source "$nuget_source" \
  -p:NuGetAudit=false \
  -m:1
dotnet build "$solution" -c Release --no-restore -m:1
dotnet run --project "$test_project" -c Release --no-build

for rid in "${runtimes[@]}"; do
  rid_root="$artifacts_root/$rid"
  rm -rf "$rid_root"
  mkdir -p "$rid_root"

  dotnet restore "$cli_project" -r "$rid" \
    --configfile "$repo_root/NuGet.Config" \
    --source "$nuget_source" \
    --source "$nuget_fallback_source" \
    -p:NuGetAudit=false \
    -m:1
  dotnet restore "$desktop_project" -r "$rid" \
    --configfile "$repo_root/NuGet.Config" \
    --source "$nuget_source" \
    --source "$nuget_fallback_source" \
    -p:NuGetAudit=false \
    -m:1
  dotnet restore "$web_project" -r "$rid" \
    --configfile "$repo_root/NuGet.Config" \
    --source "$nuget_source" \
    --source "$nuget_fallback_source" \
    -p:NuGetAudit=false \
    -m:1

  dotnet publish "$cli_project" -c Release -r "$rid" \
    --self-contained true --no-restore \
    -p:PublishSingleFile=true \
    -p:DebugType=None -p:DebugSymbols=false \
    -o "$rid_root/cli"
  dotnet publish "$desktop_project" -c Release -r "$rid" \
    --self-contained true --no-restore \
    -p:PublishSingleFile=true \
    -p:DebugType=None -p:DebugSymbols=false \
    -o "$rid_root/desktop"
  dotnet publish "$web_project" -c Release -r "$rid" \
    --self-contained true --no-restore \
    -p:PublishSingleFile=true \
    -p:DebugType=None -p:DebugSymbols=false \
    -o "$rid_root/web"
  find "$rid_root" -type f -name '*.pdb' -delete
  printf 'Published %s to %s\n' "$rid" "$rid_root"
done
