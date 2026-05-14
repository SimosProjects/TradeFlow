# Stage 1: Build
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY TradeFlow.sln ./
COPY TradeFlow.AlertPoC/TradeFlow.AlertPoC.csproj   TradeFlow.AlertPoC/
COPY TradeFlow.Worker/TradeFlow.Worker.csproj        TradeFlow.Worker/
COPY TradeFlow.Api/TradeFlow.Api.csproj              TradeFlow.Api/

COPY TradeFlow.AlertPoC/ TradeFlow.AlertPoC/
COPY TradeFlow.Worker/   TradeFlow.Worker/
COPY TradeFlow.Api/      TradeFlow.Api/

RUN dotnet publish TradeFlow.Api/TradeFlow.Api.csproj \
    --configuration Release \
    --output /app/publish \
    /p:ErrorOnDuplicatePublishOutputFiles=false

# Stage 2: Runtime
# aspnet image includes ASP.NET Core runtime (Kestrel etc.)
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# Create non-root user, security hardening
RUN groupadd --system appgroup && \
    useradd --system --gid appgroup --no-create-home appuser

COPY --from=build /app/publish .

RUN mkdir -p /app/logs && \
    chown -R appuser:appgroup /app

USER appuser

ENV DOTNET_ENVIRONMENT=Production
EXPOSE 8080

ENTRYPOINT ["dotnet", "TradeFlow.Api.dll"]