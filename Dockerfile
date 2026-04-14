FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY ["IMAJINATION BACKUP.csproj", "./"]
RUN dotnet restore "IMAJINATION BACKUP.csproj"

COPY . .
RUN dotnet publish "IMAJINATION BACKUP.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

ENV ASPNETCORE_URLS=http://0.0.0.0:10000
EXPOSE 10000

COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "IMAJINATION BACKUP.dll"]
