# syntax=docker/dockerfile:1
#
# TeamBuilder API and migration images. Two targets:
#   api       (default) the HTTP API: .NET 10 chiseled runtime, non-root, no shell, port 8080.
#   migrator  the EF Core migration bundle, run once per release before the API rolls out.
#
#   docker build -t teambuilder-api --build-arg VERSION=1.2.3 --build-arg SOURCE_REVISION=$(git rev-parse HEAD) .
#   docker build -t teambuilder-migrator --target migrator .
#
# Only src/ is copied in (see .dockerignore): no tests, docs, web client, .env or user secrets.
# Configuration and secrets (connection string, Jwt__*, WebPush__VapidPrivateKey, ...) are
# injected as environment variables at run time and are never baked into an image.

ARG DOTNET_SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
# "extra" adds ICU and tzdata to the chiseled image: IANA time zones drive game scheduling.
ARG DOTNET_ASPNET_IMAGE=mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
ARG DOTNET_RUNTIME_DEPS_IMAGE=mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled-extra

FROM ${DOTNET_SDK_IMAGE} AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
COPY .config/dotnet-tools.json .config/
COPY src/TeamBuilder.Api/TeamBuilder.Api.csproj src/TeamBuilder.Api/
COPY src/TeamBuilder.Application/TeamBuilder.Application.csproj src/TeamBuilder.Application/
COPY src/TeamBuilder.Domain/TeamBuilder.Domain.csproj src/TeamBuilder.Domain/
COPY src/TeamBuilder.Infrastructure/TeamBuilder.Infrastructure.csproj src/TeamBuilder.Infrastructure/
RUN dotnet restore src/TeamBuilder.Api/TeamBuilder.Api.csproj
COPY src/ src/

FROM build AS publish
ARG VERSION=0.0.0-local
ARG SOURCE_REVISION=unknown
RUN dotnet publish src/TeamBuilder.Api/TeamBuilder.Api.csproj \
      --configuration Release --no-restore --output /app/publish \
      -p:UseAppHost=false -p:Version=${VERSION} -p:SourceRevisionId=${SOURCE_REVISION} \
      -p:ContinuousIntegrationBuild=true -p:Deterministic=true

FROM build AS migrator-build
ARG TARGETARCH
RUN dotnet tool restore
RUN case "${TARGETARCH:-amd64}" in arm64) rid=linux-arm64 ;; *) rid=linux-x64 ;; esac \
 && dotnet tool run dotnet-ef migrations bundle \
      --project src/TeamBuilder.Infrastructure --startup-project src/TeamBuilder.Api \
      --configuration Release --self-contained --target-runtime "$rid" --output /out/efbundle

# Applies every pending migration and exits; already-applied migrations are skipped. Reads
# ConnectionStrings__TeamBuilderSql (or pass --connection). Pass a migration id as the first
# argument to move to that exact migration instead (for example to roll a test database back).
FROM ${DOTNET_RUNTIME_DEPS_IMAGE} AS migrator
ARG VERSION=0.0.0-local
ARG SOURCE_REVISION=unknown
LABEL org.opencontainers.image.title="teambuilder-migrator" \
      org.opencontainers.image.description="TeamBuilder EF Core migration bundle" \
      org.opencontainers.image.source="https://github.com/RocketDelivery2/TeamBuilder" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${SOURCE_REVISION}" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=migrator-build /out/efbundle /app/efbundle
ENV DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp
USER $APP_UID
ENTRYPOINT ["/app/efbundle"]

FROM ${DOTNET_ASPNET_IMAGE} AS api
ARG VERSION=0.0.0-local
ARG SOURCE_REVISION=unknown
LABEL org.opencontainers.image.title="teambuilder-api" \
      org.opencontainers.image.description="TeamBuilder API" \
      org.opencontainers.image.source="https://github.com/RocketDelivery2/TeamBuilder" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${SOURCE_REVISION}" \
      org.opencontainers.image.licenses="MIT"
WORKDIR /app
COPY --from=publish /app/publish .
# Plain HTTP inside the container; TLS terminates at the ingress or reverse proxy. Override the
# port with ASPNETCORE_HTTP_PORTS (not a shell ${PORT}: the image has no shell).
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# SIGTERM starts a graceful shutdown (Deployment:ShutdownTimeout, 30 s by default).
STOPSIGNAL SIGTERM
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --retries=3 \
  CMD ["dotnet", "TeamBuilder.Api.dll", "healthcheck", "live"]
ENTRYPOINT ["dotnet", "TeamBuilder.Api.dll"]
