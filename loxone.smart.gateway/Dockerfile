FROM mcr.microsoft.com/dotnet/aspnet:9.0-alpine AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:9.0-alpine AS build
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
ARG BUILDPLATFORM 

WORKDIR /src
COPY ["loxone.smart.gateway.csproj", "./"]
RUN dotnet restore "loxone.smart.gateway.csproj" -a $TARGETARCH
COPY . .
WORKDIR /src
RUN dotnet build "loxone.smart.gateway.csproj" -c $BUILD_CONFIGURATION -a $TARGETARCH -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
ARG TARGETARCH
ARG BUILDPLATFORM
RUN dotnet publish "loxone.smart.gateway.csproj" -c $BUILD_CONFIGURATION -o /app/publish -a $TARGETARCH

FROM --platform=$BUILDPLATFORM base AS final
ARG TARGETARCH
ARG BUILDPLATFORM

RUN adduser --disabled-password \
    --home /app \
    --gecos '' dotnetuser && chown -R dotnetuser /app 

USER dotnetuser

WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "loxone.smart.gateway.dll"]
