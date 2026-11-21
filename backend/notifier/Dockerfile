# One image per project: --build-arg PROJECT=Notifier.Worker (default) or Notifier.MockGateway.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT=Notifier.Worker
WORKDIR /src
COPY . .
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG PROJECT=Notifier.Worker
ENV APP_DLL=${PROJECT}.dll
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["sh", "-c", "exec dotnet $APP_DLL"]
