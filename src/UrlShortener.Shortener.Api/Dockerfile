# Build context is the repository root (see deploy/docker-compose.yml).

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first, from project files only, so this slow layer is reused until a package changes.
COPY Directory.Build.props nuget.config .editorconfig ./
COPY src/UrlShortener.Core/UrlShortener.Core.csproj src/UrlShortener.Core/
COPY src/UrlShortener.Infrastructure/UrlShortener.Infrastructure.csproj src/UrlShortener.Infrastructure/
COPY src/UrlShortener.Shortener.Api/UrlShortener.Shortener.Api.csproj src/UrlShortener.Shortener.Api/
RUN dotnet restore src/UrlShortener.Shortener.Api/UrlShortener.Shortener.Api.csproj

COPY src/UrlShortener.Core/ src/UrlShortener.Core/
COPY src/UrlShortener.Infrastructure/ src/UrlShortener.Infrastructure/
COPY src/UrlShortener.Shortener.Api/ src/UrlShortener.Shortener.Api/
RUN dotnet publish src/UrlShortener.Shortener.Api/UrlShortener.Shortener.Api.csproj \
    -c Release --no-restore -o /app/publish /p:UseAppHost=false

# Alpine (not the shell-less "chiseled" image) because the compose health check needs wget.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app
COPY --from=build /app/publish .

# The base image provides a non-root "app" user; it listens on 8080 (ASPNETCORE_HTTP_PORTS).
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "UrlShortener.Shortener.Api.dll"]
