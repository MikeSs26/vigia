# Chiselled runtime: no shell, no package manager, runs as a non-root user by
# default. Smaller image and a smaller attack surface than the full runtime tag.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./
COPY Directory.Packages.props ./
COPY vigia.sln ./
COPY src/ ./src/
RUN dotnet publish src/Vigia.Api/Vigia.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# The host has 1 GB of RAM. Server GC reserves a heap per core and would fight
# PostgreSQL for the same memory; Workstation GC keeps the footprint small.
ENV DOTNET_gcServer=0 \
    DOTNET_GCConserveMemory=5 \
    ASPNETCORE_HTTP_PORTS=8080

EXPOSE 8080
ENTRYPOINT ["dotnet", "Vigia.Api.dll"]
