FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN mv global.json /tmp/global.json \
    && dotnet restore anti-scraping-defense-iis.sln \
    && dotnet publish RedisBlocklistMiddlewareApp/RedisBlocklistMiddlewareApp.csproj \
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

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime-base
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
