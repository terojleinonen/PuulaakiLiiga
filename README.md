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
- **Forgot password (email):** set up SMTP and a public URL (below); the sign-in page then shows "Forgot password?".
  The user gets a single-use link that is valid for 30 minutes (only a hash of the token is stored). The answer is
  identical whether or not the account exists. Admins set each user's email on the Users page.
- **Forgot the admin password (no email):** run on the server
  `dotnet PuulaakiLiiga.dll reset-password <username>` — it prints a new random password and clears the lockout.

### Email settings

| Setting (env var) | Meaning |
|---|---|
| `App__PublicUrl` | Public address used in reset links, e.g. `https://league.example.com`. Required; never taken from the request. |
| `Smtp__Host`, `Smtp__Port` (587) | SMTP server |
| `Smtp__User`, `Smtp__Password` | SMTP login (optional) |
| `Smtp__From` | Sender address |
| `Smtp__EnableSsl` (true) | STARTTLS/SSL |

Without `Smtp__Host` and `App__PublicUrl` the feature is off and the link is hidden.

## Database and migrations

Schema changes use EF Core migrations, applied automatically at startup. Databases created by earlier versions
(before migrations existed) are adopted automatically. To change the model:

    dotnet tool install --global dotnet-ef
    dotnet ef migrations add <Name> --project TeamManagerClassLibrary --startup-project PuulaakiLiiga

## Docker (with HTTPS)

`docker-compose.yml` runs the app plus [Caddy](https://caddyserver.com), which terminates HTTPS:

    docker compose up --build        # https://localhost:8443

Locally Caddy uses its own certificate authority, so the browser shows a warning until you run
`docker compose exec caddy caddy trust` (or just accept it). For a real domain:

    SITE_ADDRESS=league.example.com PUBLIC_URL=https://league.example.com \
    SMTP_HOST=smtp.example.com SMTP_USER=... SMTP_PASSWORD=... SMTP_FROM=league@example.com \
    docker compose up -d

and change the port mappings in `docker-compose.yml` to `80:80` / `443:443`; Caddy then fetches a Let's Encrypt
certificate automatically. Data lives in the `puulaakiliiga-data` volume.
Without Docker, put any HTTPS reverse proxy in front of the app, or set `Auth__SecureCookies=false` for plain http.
