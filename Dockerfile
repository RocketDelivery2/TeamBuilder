FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/TeamBuilder.Api/TeamBuilder.Api.csproj src/TeamBuilder.Api/
COPY src/TeamBuilder.Application/TeamBuilder.Application.csproj src/TeamBuilder.Application/
COPY src/TeamBuilder.Domain/TeamBuilder.Domain.csproj src/TeamBuilder.Domain/
COPY src/TeamBuilder.Infrastructure/TeamBuilder.Infrastructure.csproj src/TeamBuilder.Infrastructure/

RUN dotnet restore src/TeamBuilder.Api/TeamBuilder.Api.csproj

COPY . .
RUN dotnet publish src/TeamBuilder.Api/TeamBuilder.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["sh", "-c", "ASPNETCORE_URLS=http://0.0.0.0:${PORT:-8080} dotnet TeamBuilder.Api.dll"]