FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore from the project files alone so the layer survives source-only changes. Restoring the
# API pulls in the four projects it references; the test projects are not part of the image.
COPY src/AuthCenter.Api/AuthCenter.Api.csproj src/AuthCenter.Api/
COPY src/AuthCenter.Application/AuthCenter.Application.csproj src/AuthCenter.Application/
COPY src/AuthCenter.Contracts/AuthCenter.Contracts.csproj src/AuthCenter.Contracts/
COPY src/AuthCenter.Domain/AuthCenter.Domain.csproj src/AuthCenter.Domain/
COPY src/AuthCenter.Infrastructure/AuthCenter.Infrastructure.csproj src/AuthCenter.Infrastructure/
RUN dotnet restore src/AuthCenter.Api/AuthCenter.Api.csproj

COPY src/ src/
RUN dotnet publish src/AuthCenter.Api/AuthCenter.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# curl backs the compose healthcheck; the runtime image ships without it.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Serilog writes to logs/ relative to the content root. "app" is the non-root user the .NET
# runtime images already provide.
RUN mkdir -p /app/logs && chown -R app:app /app
USER app

COPY --from=build --chown=app:app /app ./

EXPOSE 8080
ENV ASPNETCORE_HTTP_PORTS=8080

ENTRYPOINT ["dotnet", "AuthCenter.Api.dll"]
