FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/Shoreline.Api/Shoreline.Api.csproj src/Shoreline.Api/packages.lock.json src/Shoreline.Api/
RUN dotnet restore src/Shoreline.Api --locked-mode
COPY src/Shoreline.Api/ src/Shoreline.Api/
RUN dotnet publish src/Shoreline.Api -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_gcServer=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "Shoreline.Api.dll"]
