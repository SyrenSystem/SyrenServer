FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /source
COPY Syren.Server/Syren.Server.csproj Syren.Server/
RUN dotnet restore Syren.Server/Syren.Server.csproj

COPY Syren.Server/ Syren.Server/
RUN dotnet publish Syren.Server/Syren.Server.csproj --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app
COPY --from=build /app/ ./

ENTRYPOINT ["dotnet", "Syren.Server.dll"]

