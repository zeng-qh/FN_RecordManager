# 多阶段构建：同时支持 linux/amd64 与 linux/arm64（树莓派 4B 为 arm64）
# 基础镜像均为微软官方多架构镜像，无需修改即可跨平台。
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY *.csproj ./
RUN dotnet restore
COPY . .
# 去除 PDB 符号：运行时不需要，直接省体积
RUN dotnet publish -c Release -o /app --no-restore \
    -p:DebugType=none -p:DebugSymbols=false

# alpine 运行时：比 Debian 基底小约 100MB。
# 已设 invariant globalization，无需 ICU；SQLitePCLRaw 自带 musl 原生库，兼容 alpine。
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app
# curl 仅供容器健康检查；android-tools 提供 adb，供「手机无线导入」连接手机（约 +10MB）
RUN apk add --no-cache curl android-tools
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
# 镜像级健康检查：docker run / compose 通吃，不健康时可配合 autoheal 等自动重启
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD curl -fsS -o /dev/null http://localhost:8080/api/health || exit 1
ENTRYPOINT ["dotnet", "VoiceVault.Server.dll"]
