FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS build
WORKDIR /src

# Copy only what the three published projects need. A broad `COPY . .` also copies whatever sits in the build context,
# including the .env that scripts/configure_credentials.py writes, into the layers of the build stage and any shared
# build cache (#194). Restore from the project files alone first so that layer is cached until a dependency changes.
COPY Directory.Build.props global.json ./
COPY AiScrapingDefense.Contracts/*.csproj AiScrapingDefense.Contracts/
COPY AiScrapingDefense.EscalationEngine/*.csproj AiScrapingDefense.EscalationEngine/
COPY AiScrapingDefense.TarpitApi/*.csproj AiScrapingDefense.TarpitApi/
COPY RedisBlocklistMiddlewareApp/*.csproj RedisBlocklistMiddlewareApp/
RUN dotnet restore RedisBlocklistMiddlewareApp/RedisBlocklistMiddlewareApp.csproj

COPY AiScrapingDefense.Contracts/ AiScrapingDefense.Contracts/
COPY AiScrapingDefense.EscalationEngine/ AiScrapingDefense.EscalationEngine/
COPY AiScrapingDefense.TarpitApi/ AiScrapingDefense.TarpitApi/
COPY RedisBlocklistMiddlewareApp/ RedisBlocklistMiddlewareApp/
RUN dotnet publish RedisBlocklistMiddlewareApp/RedisBlocklistMiddlewareApp.csproj \
        -c Release \
        -o /app/edge \
        /p:UseAppHost=false \
    && dotnet publish AiScrapingDefense.EscalationEngine/AiScrapingDefense.EscalationEngine.csproj \
        -c Release \
        -o /app/escalation \
        /p:UseAppHost=false \
    && dotnet publish AiScrapingDefense.TarpitApi/AiScrapingDefense.TarpitApi.csproj \
        -c Release \
        -o /app/tarpit \
        /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.13 AS runtime-base
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

RUN mkdir -p /app/data
VOLUME ["/app/data"]
EXPOSE 8080

FROM runtime-base AS escalation
COPY --from=build /app/escalation .
ENTRYPOINT ["dotnet", "AiScrapingDefense.EscalationEngine.dll"]

FROM runtime-base AS tarpit
COPY --from=build /app/tarpit .
ENTRYPOINT ["dotnet", "AiScrapingDefense.TarpitApi.dll"]

FROM runtime-base AS edge
COPY --from=build /app/edge .
ENTRYPOINT ["dotnet", "AiScrapingDefense.EdgeGateway.dll"]
