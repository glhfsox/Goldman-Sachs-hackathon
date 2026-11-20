# Debian-based images: Confluent.Kafka ships librdkafka for glibc (Alpine needs extra care).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ src/
RUN dotnet publish src/Ingest.Api/Ingest.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8001
EXPOSE 8001
ENTRYPOINT ["dotnet", "Ingest.Api.dll"]
