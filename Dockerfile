FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY BibleEcho.Api.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release --no-restore -o /out -p:UseAppHost=false
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
USER $APP_UID
ENTRYPOINT ["dotnet","BibleEcho.Api.dll"]
