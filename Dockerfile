FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY ["backend/UniReserve.Api/UniReserve.Api.csproj", "backend/UniReserve.Api/"]
RUN dotnet restore "backend/UniReserve.Api/UniReserve.Api.csproj"

COPY . .
WORKDIR "/src/backend/UniReserve.Api"
RUN dotnet publish "UniReserve.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

EXPOSE 8080

ENTRYPOINT ["dotnet", "UniReserve.Api.dll"]
