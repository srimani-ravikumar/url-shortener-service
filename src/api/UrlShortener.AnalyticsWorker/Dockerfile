# Build context is the repository root (see deploy/docker-compose.yml).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from project files only, so this slow layer is reused until a package changes.
COPY Directory.Build.props Directory.Packages.props nuget.config .editorconfig ./
COPY src/api/UrlShortener.Core/UrlShortener.Core.csproj src/api/UrlShortener.Core/
COPY src/api/UrlShortener.Infrastructure/UrlShortener.Infrastructure.csproj src/api/UrlShortener.Infrastructure/
COPY src/api/UrlShortener.AnalyticsWorker/UrlShortener.AnalyticsWorker.csproj src/api/UrlShortener.AnalyticsWorker/
RUN dotnet restore src/api/UrlShortener.AnalyticsWorker/UrlShortener.AnalyticsWorker.csproj

COPY src/api/UrlShortener.Core/ src/api/UrlShortener.Core/
COPY src/api/UrlShortener.Infrastructure/ src/api/UrlShortener.Infrastructure/
COPY src/api/UrlShortener.AnalyticsWorker/ src/api/UrlShortener.AnalyticsWorker/
RUN dotnet publish src/api/UrlShortener.AnalyticsWorker/UrlShortener.AnalyticsWorker.csproj \
    -c Release --no-restore -o /app/publish /p:UseAppHost=false

# Alpine (not the shell-less "chiseled" image) because the compose health check needs wget.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
COPY --from=build /app/publish .

# The base image provides a non-root "app" user. The health port is set by the "Urls" setting in compose.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "UrlShortener.AnalyticsWorker.dll"]
