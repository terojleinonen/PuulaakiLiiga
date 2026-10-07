using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using TeamManagerClassLibrary;

static class PasswordHasher
{
    const int Iterations = 210_000;
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }
    public static bool Verify(string password, string stored)
    {
        var p = stored.Split('.');
        if (p.Length != 3 || !int.TryParse(p[0], out var iter)) return false;
        var expected = Convert.FromBase64String(p[2]);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(p[1]), iter, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}

record Credentials(string? Username, string? Password);
record PasswordChange(string? Current, string? New);
record UserInput(string? Username, string? Password, string? Role, string? Email);
record UserView(int Id, string Username, string Role, string? Email);
record ForgotRequest(string? Identifier);
record ResetRequest(string? Token, string? Password);

static class AuthEndpoints
{
    static readonly string DummyHash = PasswordHasher.Hash("dummy-password");
    const int MinPassword = 8;

    static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    static string? CleanEmail(string? e) => string.IsNullOrWhiteSpace(e) ? null : e.Trim().ToLowerInvariant();
    static bool ValidEmail(string? e) => e == null || (e.Length <= 200 && System.Net.Mail.MailAddress.TryCreate(e, out var m) && m.Address == e);

    static string Norm(string? u) => (u ?? "").Trim().ToLowerInvariant();

    static Task SignIn(HttpContext http, User u) => http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()), new Claim(ClaimTypes.Name, u.Username), new Claim(ClaimTypes.Role, u.Role), new Claim("sv", u.SessionVersion.ToString())],
            CookieAuthenticationDefaults.AuthenticationScheme)),
        new AuthenticationProperties { IsPersistent = true });

    static string? Validate(string username, string? password, string? role, bool passwordRequired)
    {
        if (username.Length is < 3 or > 40) return "Username must be 3–40 characters.";
        if (role != null && !Roles.All.Contains(role)) return "Unknown role.";
        if ((passwordRequired || !string.IsNullOrEmpty(password)) && (password?.Length ?? 0) < MinPassword) return $"Password must be at least {MinPassword} characters.";
        return null;
    }

    public static void Map(WebApplication app, RouteGroupBuilder api)
    {
        var auth = app.MapGroup("/api/auth");

        auth.MapGet("/me", async (HttpContext http, LeagueContext db) =>
        {
            if (http.User.Identity?.IsAuthenticated == true)
                return Results.Ok(new { username = http.User.Identity.Name, role = http.User.FindFirstValue(ClaimTypes.Role) });
            return await db.Users.AnyAsync() ? Results.Unauthorized() : Results.Ok(new { needsSetup = true });
        });

        // Only works while there are no users: creates the first admin.
        auth.MapPost("/setup", async (HttpContext http, LeagueContext db, Credentials c) =>
        {
            if (await db.Users.AnyAsync()) return Results.Conflict(new { error = "Already set up." });
            var name = Norm(c.Username);
            if (Validate(name, c.Password, Roles.Admin, true) is { } err) return Results.BadRequest(new { error = err });
            var u = new User { Username = name, PasswordHash = PasswordHasher.Hash(c.Password!), Role = Roles.Admin };
            db.Users.Add(u);
            await db.SaveChangesAsync();
            await SignIn(http, u);
            return Results.Ok(new { username = u.Username, role = u.Role });
        });

        // Failed attempts are stored on the user. From the 5th failure the account is locked for
        // 1, 2, 4, … minutes (max 60) until a correct login or a password reset clears it.
        // Locked and unknown accounts get the same answer as a wrong password.
        auth.MapPost("/login", async (HttpContext http, LeagueContext db, Credentials c) =>
        {
            var name = Norm(c.Username);
            var user = await db.Users.FirstOrDefaultAsync(x => x.Username == name);
            var passwordOk = PasswordHasher.Verify(c.Password ?? "", user?.PasswordHash ?? DummyHash) && user != null;
            var locked = user?.LockedUntil > DateTime.UtcNow;
            if (!passwordOk || locked)
            {
                if (user != null && !locked)
                {
                    user.FailedLogins++;
                    if (user.FailedLogins % 5 == 0)
                        user.LockedUntil = DateTime.UtcNow.AddMinutes(Math.Min(60, 1 << Math.Min(6, user.FailedLogins / 5 - 1)));
                    await db.SaveChangesAsync();
                }
                return Results.Json(new { error = "Wrong username or password (or the account is temporarily locked)." }, statusCode: 401);
            }
            user!.FailedLogins = 0; user.LockedUntil = null;
            await db.SaveChangesAsync();
            await SignIn(http, user);
            return Results.Ok(new { username = user.Username, role = user.Role });
        });

        auth.MapPost("/password", async (HttpContext http, LeagueContext db, PasswordChange p) =>
        {
            var user = await db.Users.FindAsync(int.Parse(http.User.FindFirstValue(ClaimTypes.NameIdentifier)!));
            if (user == null || !PasswordHasher.Verify(p.Current ?? "", user.PasswordHash)) return Results.BadRequest(new { error = "Current password is wrong." });
            if ((p.New?.Length ?? 0) < MinPassword) return Results.BadRequest(new { error = $"Password must be at least {MinPassword} characters." });
            user.PasswordHash = PasswordHasher.Hash(p.New!);
            user.SessionVersion++;   // signs out every other session of this user
            await db.SaveChangesAsync();
            await SignIn(http, user);
            return Results.NoContent();
        }).RequireAuthorization();

        auth.MapGet("/config", (EmailSender mail) => new { resetEnabled = mail.Configured });

        // Always answers 204 so it cannot be used to find out which accounts exist. The mail is sent in the background
        // for the same reason (no timing difference). Links are built from App:PublicUrl, never from the Host header.
        auth.MapPost("/forgot", async (LeagueContext db, EmailSender mail, ForgotRequest r) =>
        {
            var id = Norm(r.Identifier);
            if (!mail.Configured || id.Length == 0) return Results.NoContent();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Username == id || u.Email == id);
            // at most one mail per minute per account (the token lives 30 minutes)
            if (user?.Email != null && !(user.PasswordResetExpires > DateTime.UtcNow.AddMinutes(29)))
            {
                var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
                user.PasswordResetTokenHash = HashToken(token);
                user.PasswordResetExpires = DateTime.UtcNow.AddMinutes(30);
                await db.SaveChangesAsync();
                _ = mail.SendAsync(user.Email, "Puulaakiliiga password reset",
                    $"Hi {user.Username},\n\nUse this link within 30 minutes to choose a new password:\n{mail.PublicUrl}/#reset={token}\n\nIf you did not ask for this, ignore this email.");
            }
            return Results.NoContent();
        });

        auth.MapPost("/reset", async (LeagueContext db, ResetRequest r) =>
        {
            if ((r.Password?.Length ?? 0) < MinPassword) return Results.BadRequest(new { error = $"Password must be at least {MinPassword} characters." });
            var hash = HashToken(r.Token ?? "");
            var user = await db.Users.FirstOrDefaultAsync(u => u.PasswordResetTokenHash == hash && u.PasswordResetExpires > DateTime.UtcNow);
            if (user == null) return Results.BadRequest(new { error = "This reset link is invalid or has expired." });
            user.PasswordHash = PasswordHasher.Hash(r.Password!);
            user.SessionVersion++; user.FailedLogins = 0; user.LockedUntil = null;
            user.PasswordResetTokenHash = null; user.PasswordResetExpires = null;   // single use
            await db.SaveChangesAsync();
            return Results.NoContent();
        });

        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        // ---- user management (Admin only) ----
        var users = api.MapGroup("/users").RequireAuthorization("Admin");

        users.MapGet("/", async (LeagueContext db) =>
            await db.Users.OrderBy(u => u.Username).Select(u => new UserView(u.Id, u.Username, u.Role, u.Email)).ToListAsync());

        users.MapPost("/", async (LeagueContext db, UserInput i) =>
        {
            var name = Norm(i.Username);
            var email = CleanEmail(i.Email);
            if (!ValidEmail(email)) return Results.BadRequest(new { error = "Invalid email address." });
            if (Validate(name, i.Password, i.Role ?? Roles.Viewer, true) is { } err) return Results.BadRequest(new { error = err });
            if (await db.Users.AnyAsync(u => u.Username == name)) return Results.BadRequest(new { error = "Username already exists." });
            var u = new User { Username = name, PasswordHash = PasswordHasher.Hash(i.Password!), Role = i.Role ?? Roles.Viewer, Email = email };
            db.Users.Add(u);
            await db.SaveChangesAsync();
            return Results.Ok(new UserView(u.Id, u.Username, u.Role, u.Email));
        });

        users.MapPut("/{id:int}", async (HttpContext http, LeagueContext db, int id, UserInput i) =>
        {
            var u = await db.Users.FindAsync(id);
            if (u == null) return Results.NotFound();
            var name = Norm(i.Username);
            var email = CleanEmail(i.Email);
            if (!ValidEmail(email)) return Results.BadRequest(new { error = "Invalid email address." });
            if (Validate(name, i.Password, i.Role ?? u.Role, false) is { } err) return Results.BadRequest(new { error = err });
            if (await db.Users.AnyAsync(x => x.Username == name && x.Id != id)) return Results.BadRequest(new { error = "Username already exists." });
            var newRole = i.Role ?? u.Role;
            if (u.Role == Roles.Admin && newRole != Roles.Admin && !await db.Users.AnyAsync(x => x.Role == Roles.Admin && x.Id != id))
                return Results.BadRequest(new { error = "There must be at least one admin." });
            if (newRole != u.Role || !string.IsNullOrEmpty(i.Password)) u.SessionVersion++;   // end their existing sessions
            u.Username = name; u.Role = newRole; u.Email = email;
            if (!string.IsNullOrEmpty(i.Password)) { u.PasswordHash = PasswordHasher.Hash(i.Password); u.FailedLogins = 0; u.LockedUntil = null; }
            await db.SaveChangesAsync();
            return Results.Ok(new UserView(u.Id, u.Username, u.Role, u.Email));
        });

        users.MapDelete("/{id:int}", async (HttpContext http, LeagueContext db, int id) =>
        {
            if (http.User.FindFirstValue(ClaimTypes.NameIdentifier) == id.ToString())
                return Results.BadRequest(new { error = "You cannot delete your own account." });
            var n = await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync();
            return n == 0 ? Results.NotFound() : Results.NoContent();
        });
    }
}
