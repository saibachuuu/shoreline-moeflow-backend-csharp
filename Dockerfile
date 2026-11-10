# MCR manifest digests verified 2026-11-10; amd64/arm64 multi-arch indexes.
# The SDK tag is version-specific: 10.0.403 and the floating 10.0 share this
# index digest. The pinned SDK image reports dotnet --version 10.0.403.
FROM mcr.microsoft.com/dotnet/sdk:10.0.403@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Shoreline.Api/Shoreline.Api.csproj src/Shoreline.Api/packages.lock.json src/Shoreline.Api/
COPY src/Shoreline.Imaging/Shoreline.Imaging.csproj src/Shoreline.Imaging/packages.lock.json src/Shoreline.Imaging/
RUN dotnet restore src/Shoreline.Api --locked-mode
COPY src/Shoreline.Api/ src/Shoreline.Api/
COPY src/Shoreline.Imaging/ src/Shoreline.Imaging/
RUN dotnet publish src/Shoreline.Api -c Release --no-restore -o /out /p:UseAppHost=false
# Publish creates files and directories with wall-clock mtimes. Normalize them
# before the final COPY so equivalent locked builds produce the same layer.
RUN find /out -exec touch -h -d '@0' {} +

FROM mcr.microsoft.com/dotnet/aspnet:10.0.8@sha256:8c0b6857eab7b2aa57884c839bf4678414606bd7d17370f18a842ac5cf414711
WORKDIR /app
COPY --from=build /out .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_gcServer=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "Shoreline.Api.dll"]
