FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG TARGETARCH
WORKDIR /src

COPY ProxmoxMCPSharp.csproj ./
RUN dotnet restore ProxmoxMCPSharp.csproj -a $TARGETARCH

COPY . .
RUN dotnet publish ProxmoxMCPSharp.csproj \
    -c Release \
    -a $TARGETARCH \
    --no-restore \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:5104 \
    PROXMOXMCP_Server__Host=0.0.0.0 \
    PROXMOXMCP_Server__Port=5104 \
    PROXMOXMCP_Server__Path=/mcp \
    PROXMOXMCP_Proxmox__ReadOnly=true

COPY --from=build /app/publish .

EXPOSE 5104

ENTRYPOINT ["dotnet", "ProxmoxMCPSharp.dll"]
