# syntax=docker/dockerfile:1.7

FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY NuGet.config global.json Directory.Build.props Directory.Packages.props ./
COPY ProxmoxMCPSharp.csproj ./
RUN dotnet restore ProxmoxMCPSharp.csproj

COPY . .
RUN dotnet publish ProxmoxMCPSharp.csproj \
    -c Release \
    --no-restore \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS runtime
WORKDIR /app

ENV DOTNET_ENVIRONMENT=Production \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    PROXMOXMCP_Server__Host=0.0.0.0 \
    PROXMOXMCP_Server__Port=5705 \
    PROXMOXMCP_Server__Path=/mcp \
    PROXMOXMCP_Proxmox__ReadOnly=true

RUN mkdir -p /app/logs && chown -R $APP_UID:0 /app
COPY --from=build --chown=$APP_UID:0 /app/publish ./

USER $APP_UID
EXPOSE 5705
VOLUME ["/app/logs"]

ENTRYPOINT ["dotnet", "ProxmoxMCPSharp.dll"]
