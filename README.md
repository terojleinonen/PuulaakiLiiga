# Puulaakiliiga

League manager: teams, players, coaches, contacts, games, penalties and a live league table.

ASP.NET Core (.NET 10) minimal API + EF Core + SQLite, serving the UI from `PuulaakiLiiga/wwwroot`.

    dotnet run --project PuulaakiLiiga
    # open http://localhost:5000 (see console output for the exact port)

Data is stored in `puulaakiliiga.db` (created on first run). Override with the
`ConnectionStrings__Default` environment variable. Use "Load demo data" in the UI to try it out.

## Logins and roles

On first run the UI asks you to create the admin account. After that every page and API call requires sign-in.

| Role | Can do |
|------|--------|
| Admin | Everything, including managing users and importing data |
| Manager | Add, edit and delete league data |
| Viewer | Read-only |

Passwords are hashed with PBKDF2; sessions use an HttpOnly, SameSite=Strict cookie (8 h, sliding).
Five wrong passwords lock a username for one minute. Run behind HTTPS in production.
