FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src

RUN apt-get update \
 && apt-get install -y --no-install-recommends clang zlib1g-dev \
 && rm -rf /var/lib/apt/lists/*

COPY src/Neadocs.Engine/Neadocs.Engine.csproj src/Neadocs.Engine/
RUN dotnet restore src/Neadocs.Engine/Neadocs.Engine.csproj \
      -r linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64)

COPY src/ src/
RUN dotnet publish src/Neadocs.Engine/Neadocs.Engine.csproj \
      -c Release \
      -r linux-$([ "$TARGETARCH" = "arm64" ] && echo arm64 || echo x64) \
      -o /app

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble-chiseled AS runtime
WORKDIR /app

COPY --from=build /app/Neadocs.Engine ./Neadocs.Engine
COPY --from=build /app/appsettings.json ./appsettings.json
COPY normalizers/ ./normalizers/

ENV ASPNETCORE_URLS=http://+:5700 \
    DOTNET_gcServer=1

EXPOSE 5700
USER $APP_UID

ENTRYPOINT ["./Neadocs.Engine"]
