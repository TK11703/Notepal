# Build from the repository root: docker build -f src/Notepal.Api/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json ./
COPY src/Notepal.Shared/Notepal.Shared.csproj src/Notepal.Shared/
COPY src/Notepal.Api/Notepal.Api.csproj src/Notepal.Api/
RUN dotnet restore src/Notepal.Api/Notepal.Api.csproj
COPY src/ src/
RUN dotnet publish src/Notepal.Api/Notepal.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "Notepal.Api.dll"]
