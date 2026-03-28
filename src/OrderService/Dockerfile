FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS base
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src
COPY global.json ./
COPY src/Directory.Build.props ./src/
COPY src/Shared/Shared.csproj ./src/Shared/
COPY src/OrderService/OrderService.csproj ./src/OrderService/
RUN dotnet restore src/OrderService/OrderService.csproj
COPY src/Shared/ ./src/Shared/
COPY src/OrderService/ ./src/OrderService/
RUN dotnet publish src/OrderService/OrderService.csproj -c Release -o /app/publish

FROM base AS final
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "OrderService.dll"]
