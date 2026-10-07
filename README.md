# Puulaakiliiga

League manager: teams, players, coaches, contacts, games, penalties and a live league table.

ASP.NET Core (.NET 10) minimal API + EF Core + SQLite, serving the UI from `PuulaakiLiiga/wwwroot`.

    dotnet run --project PuulaakiLiiga --launch-profile PuulaakiLiiga   # http://localhost:5000

Data is stored in `puulaakiliiga.db` (created on first run). Override with the
`ConnectionStrings__Default` environment variable. Use "Load demo data" in the UI to try it out.

## Logins and roles

On first run the UI asks you to create the admin account. After that every page and API call requires sign-in.

| Role | Can do |
|------|--------|
| Admin | Everything, including managing users and importing data |
| Manager | Add, edit and delete league data |
| Viewer | Read-only |

Passwords are hashed with PBKDF2 (210,000 rounds, min. 8 characters).

- **Sessions:** HttpOnly, SameSite=Strict cookie, 8 h sliding. The cookie is Secure-only outside development, so
  serve the app over HTTPS (e.g. behind a reverse proxy). For a plain-http trial set `Auth__SecureCookies=false`.
  Changing a user's password or role, or deleting the user, signs out their existing sessions immediately.
- **Lockout:** stored in the database (survives restarts). From the 5th wrong password the account is locked for
  1, 2, 4 … minutes (max 60). Locked and unknown accounts get the same error as a wrong password.
- **Change your password:** "Change password" in the sidebar.
- **Forgot the admin password:** run on the server
  `dotnet PuulaakiLiiga.dll reset-password <username>` — it prints a new random password and clears the lockout.

## Database and migrations

Schema changes use EF Core migrations, applied automatically at startup. Databases created by earlier versions
(before migrations existed) are adopted automatically. To change the model:

    dotnet tool install --global dotnet-ef
    dotnet ef migrations add <Name> --project TeamManagerClassLibrary --startup-project PuulaakiLiiga

## Docker

    docker compose up --build        # http://localhost:8080, data in the puulaakiliiga-data volume

Set `Auth__SecureCookies: "false"` in `docker-compose.yml` to try it over plain http; use HTTPS for real deployments.
