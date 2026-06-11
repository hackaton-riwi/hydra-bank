FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY Directory.Build.props ./

COPY Hydra.Domain/Hydra.Domain.csproj Hydra.Domain/
COPY Hydra.Application/Hydra.Application.csproj Hydra.Application/
COPY Hydra.Infrastructure/Hydra.Infrastructure.csproj Hydra.Infrastructure/
COPY Hydra.Api/Hydra.Api.csproj Hydra.Api/

RUN dotnet restore Hydra.Api/Hydra.Api.csproj

COPY . .

RUN dotnet publish Hydra.Api/Hydra.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Hydra.Api.dll"]
