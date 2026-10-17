FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/CarParkOccupancy.DataApi/CarParkOccupancy.DataApi.csproj src/CarParkOccupancy.DataApi/
RUN dotnet restore src/CarParkOccupancy.DataApi/CarParkOccupancy.DataApi.csproj

COPY src/CarParkOccupancy.DataApi/ src/CarParkOccupancy.DataApi/
RUN dotnet publish src/CarParkOccupancy.DataApi/CarParkOccupancy.DataApi.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
EXPOSE 8080
COPY --from=build /app .
ENTRYPOINT ["dotnet", "CarParkOccupancy.DataApi.dll"]
