# Serveur de support PommeBrowser : docker compose up -d --build
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY src/PommeBrowser.SupportServer/PommeBrowser.SupportServer.csproj src/PommeBrowser.SupportServer/
RUN dotnet restore src/PommeBrowser.SupportServer/PommeBrowser.SupportServer.csproj
COPY src/ src/
# Publication indépendante de l'architecture : la même image tourne sur x64 et ARM.
RUN dotnet publish src/PommeBrowser.SupportServer/PommeBrowser.SupportServer.csproj \
    --configuration Release --no-restore --output /app -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app .

ENV ASPNETCORE_HTTP_PORTS=8080 \
    SUPPORT_DATA_DIR=/data
RUN mkdir -p /data && chown "$APP_UID" /data
VOLUME /data

# Utilisateur non privilégié fourni par l'image .NET.
USER $APP_UID
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD wget -qO- http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["dotnet", "PommeBrowser.SupportServer.dll"]
