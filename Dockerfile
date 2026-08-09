# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY NuGet.Config ./
COPY src/AIHubRouter.Core/AIHubRouter.Core.csproj src/AIHubRouter.Core/
COPY src/AIHubRouter.Web/AIHubRouter.Web.csproj src/AIHubRouter.Web/

RUN dotnet restore src/AIHubRouter.Web/AIHubRouter.Web.csproj \
    --configfile NuGet.Config \
    -p:NuGetAudit=false \
    -m:1

COPY src/AIHubRouter.Core/ src/AIHubRouter.Core/
COPY src/AIHubRouter.Web/ src/AIHubRouter.Web/

RUN dotnet publish src/AIHubRouter.Web/AIHubRouter.Web.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=1 \
    XDG_CONFIG_HOME=/app/data

COPY --from=build /app/publish ./
COPY scripts/channel_detector_worker.py ./scripts/channel_detector_worker.py
COPY gpt56_api_detector ./gpt56_api_detector

RUN mkdir -p /app/data \
    && apt-get update \
    && apt-get install --no-install-recommends --yes python3 \
    && rm -rf /var/lib/apt/lists/* \
    && chown -R 10001:10001 /app

USER 10001:10001

EXPOSE 5080 5443

ENTRYPOINT ["dotnet", "aihub-router-web.dll"]
