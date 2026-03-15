FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN mv global.json /tmp/global.json \
    && dotnet restore anti-scraping-defense-iis.sln \
    && dotnet publish RedisBlocklistMiddlewareApp/RedisBlocklistMiddlewareApp.csproj \
        -c Release \
        -o /app/publish \
        /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

COPY --from=build /app/publish .
RUN mkdir -p /app/data

VOLUME ["/app/data"]
EXPOSE 8080

ENTRYPOINT ["dotnet", "AiScrapingDefense.EdgeGateway.dll"]
