FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY artifact-renderer/ArtifactRenderer.csproj artifact-renderer/
RUN dotnet restore artifact-renderer/ArtifactRenderer.csproj
COPY artifact-renderer/ artifact-renderer/
RUN dotnet publish artifact-renderer/ArtifactRenderer.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends libreoffice-core libreoffice-writer libreoffice-calc libreoffice-impress fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /tmp/render \
    && chown 1000:1000 /tmp/render
WORKDIR /app
COPY --from=build /app .
USER 1000:1000
ENV ASPNETCORE_URLS=http://+:8080 \
    Renderer__WorkRoot=/tmp/render
EXPOSE 8080
ENTRYPOINT ["dotnet", "ArtifactRenderer.dll"]
