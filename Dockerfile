# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/SplitIt/SplitIt.csproj src/SplitIt/
RUN dotnet restore src/SplitIt/SplitIt.csproj
COPY src/SplitIt src/SplitIt
RUN dotnet publish src/SplitIt/SplitIt.csproj -c Release -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
# The codex platform health-checks the container with curl.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
COPY --from=build /app ./
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "SplitIt.dll"]
