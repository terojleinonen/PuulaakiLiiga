# Puulaakiliiga

League manager: teams, players, coaches, contacts, games, penalties and a live league table.

ASP.NET Core (.NET 10) minimal API + EF Core + SQLite, serving the UI from `PuulaakiLiiga/wwwroot`.

    dotnet run --project PuulaakiLiiga
    # open http://localhost:5000 (see console output for the exact port)

Data is stored in `puulaakiliiga.db` (created on first run). Override with the
`ConnectionStrings__Default` environment variable. Use "Load demo data" in the UI to try it out.
