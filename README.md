# Puulaakiliiga

A league manager for a sports league: teams, players, coaches, contacts, games, penalties and a live league table,
with user logins and roles.

It started as a C# console app (menu-driven data entry that never saved anything) and is now a web app:
**ASP.NET Core (.NET 10)** minimal API + **EF Core** + **SQLite**, serving a dependency-free HTML/JS UI.

## Features

- **Standings:** league table (win 3 pts, draw 1) with goals for/against, penalty minutes, upcoming games, penalty leaders
- **Data pages:** teams, players, coaches, contacts, games (score optional until played), penalties — add, edit, delete, search
- **Consistent deletes:** removing a team also removes its players, games and their penalties
- **Logins and roles:** Admin, Manager, Viewer (below), plus a Users page for admins
- **Import / export** of all league data as JSON, and one-click demo data

## Quick start

    dotnet run --project PuulaakiLiiga --launch-profile PuulaakiLiiga      # http://localhost:5000

On first run the app asks you to create the admin account. Then click **Load demo data** to try it out.
The database file `puulaakiliiga.db` is created next to where you start the app.

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

## Project layout

| Path | What it is |
|---|---|
| `PuulaakiLiiga/` | Web app: `Program.cs` (setup + data API), `Auth.cs` (login, users, password reset), `Email.cs`, `wwwroot/` (UI) |
| `TeamManagerClassLibrary/` | Entities, `LeagueContext` (EF Core) and migrations |
| `tests/` | JavaScript tests for the standings, xUnit tests for roles and logins |
| `Dockerfile`, `docker-compose.yml`, `Caddyfile` | Container deployment with HTTPS |

## Configuration

Set as environment variables (`__` separates sections) or in `appsettings.json`.

| Setting | Default | Meaning |
|---|---|---|
| `ConnectionStrings__Default` | `Data Source=puulaakiliiga.db` | SQLite database file |
| `Auth__SecureCookies` | `true` outside development | Send the session cookie over HTTPS only. Set `false` only for plain-http setups |
| `App__PublicUrl` | – | Public address used in password-reset links, e.g. `https://league.example.com` |
| `Smtp__Host` | – | SMTP server. With `App__PublicUrl` set, this turns on "Forgot password?" |
| `Smtp__Port` / `Smtp__EnableSsl` | `587` / `true` | SMTP port and STARTTLS/SSL |
| `Smtp__User` / `Smtp__Password` | – | SMTP login (optional) |
| `Smtp__From` | – | Sender address |

## Logins and roles

| Role | Can do |
|------|--------|
| Admin | Everything, including managing users and importing data |
| Manager | Add, edit and delete league data |
| Viewer | Read-only |

Every page and API call requires sign-in. Roles are enforced on the server, not just in the UI.

**Security details**

- Passwords: PBKDF2-SHA256, 210,000 iterations, minimum 8 characters.
- Sessions: HttpOnly, SameSite=Strict cookie, 8 h sliding. Changing a user's password or role, or deleting the user,
  signs out their existing sessions immediately.
- Lockout: stored in the database, so it survives restarts. From the 5th wrong password an account is locked for
  1, 2, 4 … minutes (max 60). Locked and unknown accounts get the same error as a wrong password.
- You cannot delete your own account or demote the last admin.

**Password recovery**

- *Change your own:* "Change password" in the sidebar.
- *By email:* needs `App__PublicUrl` and `Smtp__Host`. The sign-in page then shows "Forgot password?". The user gets a
  single-use link valid for 30 minutes (only a hash of the token is stored); at most one mail per minute per account;
  the response is identical whether or not the account exists. Admins set each user's email on the Users page.
- *Without email (server access):* `dotnet PuulaakiLiiga.dll reset-password <username>` prints a new random
  password and clears any lockout. In Docker: `docker exec <container> dotnet PuulaakiLiiga.dll reset-password <username>`.

## Docker (with HTTPS)

`docker-compose.yml` runs the app plus [Caddy](https://caddyserver.com), which terminates HTTPS:

    docker compose up --build            # https://localhost:8443

Locally Caddy uses its own certificate authority, so the browser warns until you run
`docker compose exec caddy caddy trust` (or accept the warning). For a real domain:

    SITE_ADDRESS=league.example.com PUBLIC_URL=https://league.example.com \
    SMTP_HOST=smtp.example.com SMTP_USER=... SMTP_PASSWORD=... SMTP_FROM=league@example.com \
    docker compose up -d

and change the port mappings in `docker-compose.yml` to `80:80` / `443:443`; Caddy then obtains a Let's Encrypt
certificate automatically. The database lives in the `puulaakiliiga-data` volume; back it up like any other file.

Without Docker, put any HTTPS reverse proxy in front of the app.

## Tests

    node --test tests/js/*.test.js     # league table rules (win/draw points, tie-breaks, penalty minutes, bad data)
    dotnet test                        # roles and login rules, against the real app on a throw-away database

- `tests/js` tests `PuulaakiLiiga/wwwroot/standings.js`, the pure function behind the standings page.
- `tests/PuulaakiLiiga.Tests` starts the app with `WebApplicationFactory` and checks the role matrix (anonymous / Viewer /
  Manager / Admin on every endpoint), first-run setup, user management rules (unique names, last admin, own account),
  session invalidation, lockout and password change/reset.

Both run in CI on every push and pull request.

## Database and migrations

Schema changes use EF Core migrations, applied automatically at startup. Databases created by earlier versions
(before migrations existed) are adopted automatically. To change the model:

    dotnet tool install --global dotnet-ef
    dotnet ef migrations add <Name> --project TeamManagerClassLibrary --startup-project PuulaakiLiiga

To use another database (e.g. MySQL), swap the EF provider package and the `UseSqlite` call in `Program.cs`, then
regenerate the migrations for that provider.

## API

All under `/api`, JSON, cookie-authenticated.

| Endpoint | Who |
|---|---|
| `GET /data` | any signed-in user — all league data |
| `POST/PUT/DELETE /{teams,players,coaches,contacts,games,penalties}[/{id}]` | Admin, Manager |
| `POST /import` | Admin — replaces all league data |
| `GET/POST/PUT/DELETE /users` | Admin |
| `/auth/{me,setup,login,logout,password,config,forgot,reset}` | see `Auth.cs` |
