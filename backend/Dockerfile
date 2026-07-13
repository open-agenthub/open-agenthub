# ---- build ----
# Built from the repo ROOT (see .github/workflows/build-images.yml) so the
# enterprise sources in ../ee/backend (referenced by the .csproj) are in context.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY backend/AgentHub.Api.csproj backend/
RUN dotnet restore backend/AgentHub.Api.csproj
COPY backend/ backend/
COPY ee/ ee/
RUN dotnet publish backend/AgentHub.Api.csproj -c Release -o /app --no-restore

# ---- runtime (non-root) ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# distroless-like: non-root user 1000
USER 1000:1000
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "AgentHub.Api.dll"]
