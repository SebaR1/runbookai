# System API. Multi-stage: the SDK compiles (and NSwag generates the RAG client
# from Rag/rag-service.openapi.json); the runtime image only carries the result.
FROM mcr.microsoft.com/dotnet/sdk:10.0.404 AS build
WORKDIR /src

# Restore in its own layer: changing the code does not download packages again.
COPY src/RunbookAI.SystemApi/RunbookAI.SystemApi.csproj src/RunbookAI.SystemApi/
RUN dotnet restore src/RunbookAI.SystemApi/RunbookAI.SystemApi.csproj

COPY src/RunbookAI.SystemApi/ src/RunbookAI.SystemApi/
RUN dotnet publish src/RunbookAI.SystemApi/RunbookAI.SystemApi.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15
WORKDIR /app
COPY --from=build /app .

# Non-root user that the .NET images already define.
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "RunbookAI.SystemApi.dll"]
