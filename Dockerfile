FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore src/RemoteDesk.Signaling/RemoteDesk.Signaling.csproj
RUN dotnet publish src/RemoteDesk.Signaling/RemoteDesk.Signaling.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "RemoteDesk.Signaling.dll"]

