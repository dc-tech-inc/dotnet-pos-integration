# Two runnable targets from one build: `worker` (the integration) and `fakepos` (the fictional POS API).
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props .editorconfig PosIntegration.sln global.json ./
COPY src ./src
COPY tests/PosIntegration.Tests/PosIntegration.Tests.csproj ./tests/PosIntegration.Tests/
RUN dotnet restore src/PosIntegration.Worker && dotnet restore src/PosIntegration.FakePos
RUN dotnet publish src/PosIntegration.Worker -c Release --no-restore -o /out/worker \
 && dotnet publish src/PosIntegration.FakePos -c Release --no-restore -o /out/fakepos

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS worker
WORKDIR /app
COPY --from=build /out/worker ./
USER $APP_UID
ENTRYPOINT ["dotnet", "PosIntegration.Worker.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS fakepos
WORKDIR /app
COPY --from=build /out/fakepos ./
ENV ASPNETCORE_URLS=http://+:5080
EXPOSE 5080
USER $APP_UID
ENTRYPOINT ["dotnet", "PosIntegration.FakePos.dll"]
