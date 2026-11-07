# =======================================
# Stage 1: Restore + Build + Publish
# =======================================
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy csproj files for restore (caching)
COPY ["Hospital_CRM.Api/Hospital_CRM.Api.csproj", "Hospital_CRM.Api/"]
COPY ["Hospital_CRM.Application/Hospital_CRM.Application.csproj", "Hospital_CRM.Application/"]
COPY ["Hospital_CRM.Domain/Hospital_CRM.Domain.csproj", "Hospital_CRM.Domain/"]
COPY ["Hospital_CRM.Infrastructure/Hospital_CRM.Infrastructure.csproj", "Hospital_CRM.Infrastructure/"]

RUN dotnet restore "Hospital_CRM.Api/Hospital_CRM.Api.csproj"

# Copy the rest of the source
COPY . .

RUN dotnet publish "Hospital_CRM.Api/Hospital_CRM.Api.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# =======================================
# Stage 2: Runtime
# =======================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Install healthcheck tooling and the prescription renderer's native dependency
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*

# Non-root user for production
RUN groupadd --gid 1001 appuser \
    && useradd --uid 1001 --gid appuser --shell /bin/bash --create-home appuser

# Copy published output
COPY --from=build /app/publish .

# Ensure keys and lab uploads directories exist with proper permissions
RUN mkdir -p /app/keys /app/lab-uploads && chown -R appuser:appuser /app

USER appuser

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
ENV DOTNET_RUNNING_IN_CONTAINER=true

EXPOSE 8080

ENTRYPOINT ["dotnet", "Hospital_CRM.Api.dll"]
