# Build context is the repository root. Central Package Management resolves
# versions from Directory.Packages.props, so both it and Directory.Build.props
# must be present before `dotnet restore`/`publish` runs, or restore fails.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS base
WORKDIR /src

COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY vigia.sln ./
COPY src/ ./src/

# --- API image -------------------------------------------------------------

FROM base AS publish
RUN dotnet publish src/Vigia.Api/Vigia.Api.csproj -c Release -o /app/publish

# Chiselled runtime: no shell, no package manager, runs as a non-root user by
# default. Smaller image and a smaller attack surface than the full runtime tag.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=publish /app/publish .

# The host has 1 GB of RAM. Server GC reserves a heap per core and would fight
# PostgreSQL for the same memory; Workstation GC keeps the footprint small.
ENV DOTNET_gcServer=0 \
    DOTNET_GCConserveMemory=5 \
    ASPNETCORE_HTTP_PORTS=8080

EXPOSE 8080
ENTRYPOINT ["dotnet", "Vigia.Api.dll"]

# --- Migration bundle --------------------------------------------------------
#
# Migrations must not run at application startup (see design doc §16): with
# more than one api replica that races. Instead `dotnet ef migrations bundle`
# produces a self-contained executable that a one-shot compose service runs
# once, ahead of any api instance. Building it here, on its own branch from
# `base`, keeps EF tooling and the SDK entirely out of the api image.

FROM base AS bundle
RUN dotnet tool install --global dotnet-ef --version 10.0.10
ENV PATH="$PATH:/root/.dotnet/tools"
# `dotnet ef migrations bundle` does not restore on its own the way `dotnet
# publish`/`build` do; the self-contained runtime packages for linux-x64 need
# to be present before it runs.
RUN dotnet restore src/Vigia.Infrastructure/Vigia.Infrastructure.csproj -r linux-x64
RUN dotnet ef migrations bundle \
      --project src/Vigia.Infrastructure \
      --startup-project src/Vigia.Infrastructure \
      --configuration Release \
      --self-contained -r linux-x64 \
      -o /app/efbundle

# runtime-deps, not aspnet/runtime: the bundle is self-contained and carries
# its own .NET runtime, so the image only needs the native OS dependencies
# (libssl, libicu, ...) underneath it.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS migrate
WORKDIR /app
COPY --from=bundle /app/efbundle .

# Chiselled runtime-deps has no ICU/tzdata; the bundle never formats anything
# culture-specific, so invariant mode avoids depending on data that isn't there.
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true

ENTRYPOINT ["/app/efbundle"]
