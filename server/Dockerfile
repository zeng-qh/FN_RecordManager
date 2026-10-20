# 多阶段构建：同时支持 linux/amd64 与 linux/arm64（树莓派 4B 为 arm64）
# 基础镜像均为微软官方多架构镜像，无需修改即可跨平台。
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY *.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV DOTNET_RUNNING_IN_CONTAINER=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_GCHeapHardLimitPercent=70 \
    DOTNET_gcServer=0 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    DOTNET_EnableDiagnostics=0 \
    VAULT_DATADIR=/data \
    VAULT_PORT=8080 \
    VAULT_BIND=0.0.0.0
COPY --from=build /app .
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "VoiceVault.Server.dll"]
