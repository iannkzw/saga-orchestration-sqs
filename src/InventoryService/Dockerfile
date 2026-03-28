FROM mcr.microsoft.com/dotnet/aspnet:10.0-preview AS base
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:10.0-preview AS build
WORKDIR /src
COPY global.json ./
COPY src/Directory.Build.props ./src/
COPY src/Shared/Shared.csproj ./src/Shared/
COPY src/InventoryService/InventoryService.csproj ./src/InventoryService/
RUN dotnet restore src/InventoryService/InventoryService.csproj
COPY src/Shared/ ./src/Shared/
COPY src/InventoryService/ ./src/InventoryService/
RUN dotnet publish src/InventoryService/InventoryService.csproj -c Release -o /app/publish

FROM base AS final
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "InventoryService.dll"]
