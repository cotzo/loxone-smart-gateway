FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["loxone.smart.gateway.csproj", "./"]
RUN dir -R
RUN dir -R /app; exit 0
RUN dotnet restore "loxone.smart.gateway.csproj"
RUN dir -R
COPY . .
RUN dir -R
RUN dir -R /app; exit 0
WORKDIR /src
RUN dir -R
RUN dir -R /app; exit 0
RUN dotnet build "loxone.smart.gateway.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dir -R /app; exit 0
RUN dotnet publish "loxone.smart.gateway.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "loxone.smart.gateway.dll"]
