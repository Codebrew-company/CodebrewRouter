FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# ── Layer project files for cached restore ──
COPY Blaze.LlmGateway.slnx .
COPY nuget.config .
COPY Blaze.LlmGateway.Api/Blaze.LlmGateway.Api.csproj Blaze.LlmGateway.Api/
COPY Blaze.LlmGateway.Core/Blaze.LlmGateway.Core.csproj Blaze.LlmGateway.Core/
COPY Blaze.LlmGateway.Infrastructure/Blaze.LlmGateway.Infrastructure.csproj Blaze.LlmGateway.Infrastructure/
COPY Blaze.LlmGateway.LocalInference/Blaze.LlmGateway.LocalInference.csproj Blaze.LlmGateway.LocalInference/
COPY Blaze.LlmGateway.ServiceDefaults/Blaze.LlmGateway.ServiceDefaults.csproj Blaze.LlmGateway.ServiceDefaults/

RUN dotnet restore Blaze.LlmGateway.Api/Blaze.LlmGateway.Api.csproj

# ── Build & publish ──
COPY . .
WORKDIR /src/Blaze.LlmGateway.Api
RUN dotnet publish -c Release -o /app \
    --no-restore

# ── Runtime ──
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# System deps for LM-Kit native binaries
RUN apt-get update && apt-get install -y --no-install-recommends \
    libgomp1 \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app .

# LM-Kit model cache — mount a compose-managed named volume (llm-cache) here.
# Do NOT use Dockerfile VOLUME — it creates an anonymous volume that conflicts
# with the compose volume mount and causes model file corruption on recreate.

ENV ASPNETCORE_URLS=http://0.0.0.0:5022
EXPOSE 5022

ENTRYPOINT ["dotnet", "Blaze.LlmGateway.Api.dll"]
