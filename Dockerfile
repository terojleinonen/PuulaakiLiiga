FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY PuulaakiLiiga.sln .
COPY PuulaakiLiiga/PuulaakiLiiga.csproj PuulaakiLiiga/
COPY TeamManagerClassLibrary/TeamManagerClassLibrary.csproj TeamManagerClassLibrary/
RUN dotnet restore PuulaakiLiiga/PuulaakiLiiga.csproj
COPY . .
RUN dotnet publish PuulaakiLiiga/PuulaakiLiiga.csproj -c Release -o /out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /out .
# The database lives in /data: mount a volume there to keep it.
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Default="Data Source=/data/puulaakiliiga.db"
RUN mkdir /data && chown $APP_UID /data
VOLUME /data
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "PuulaakiLiiga.dll"]
