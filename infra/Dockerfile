FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/Api/Api.csproj .
RUN dotnet restore
COPY src/Api/ .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .

# Submissão: imagem AUTO-CONTIDA. O índice (3M referências) e o mcc_risk vão DENTRO da
# imagem — assim o juiz só dá `docker compose up` da imagem publicada, sem volume externo.
COPY data/index.bin /app/data/index.bin
COPY data/mcc_risk.json /app/data/mcc_risk.json
ENV DATA_PATH=/app/data

ENTRYPOINT ["dotnet", "Api.dll"]
