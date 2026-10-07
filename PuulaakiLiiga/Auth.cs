using System.Collections.Concurrent;
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
record UserInput(string? Username, string? Password, string? Role);
record UserView(int Id, string Username, string Role);

static class AuthEndpoints
{
    // After 5 wrong passwords a username is locked for a minute (in-memory, per process).
    static readonly ConcurrentDictionary<string, (int Fails, DateTime Until)> failures = new();
    static readonly string DummyHash = PasswordHasher.Hash("dummy-password");
    const int MinPassword = 8;

    static string Norm(string? u) => (u ?? "").Trim().ToLowerInvariant();

    static Task SignIn(HttpContext http, User u) => http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()), new Claim(ClaimTypes.Name, u.Username), new Claim(ClaimTypes.Role, u.Role)],
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

        auth.MapPost("/login", async (HttpContext http, LeagueContext db, Credentials c) =>
        {
            var name = Norm(c.Username);
            if (failures.TryGetValue(name, out var f) && f.Until > DateTime.UtcNow)
                return Results.Json(new { error = "Too many attempts. Try again in a minute." }, statusCode: 429);
            var user = await db.Users.FirstOrDefaultAsync(x => x.Username == name);
            var ok = PasswordHasher.Verify(c.Password ?? "", user?.PasswordHash ?? DummyHash) && user != null;
            if (!ok)
            {
                var fails = failures.TryGetValue(name, out var prev) ? prev.Fails + 1 : 1;
                failures[name] = (fails, fails >= 5 ? DateTime.UtcNow.AddMinutes(1) : DateTime.MinValue);
                return Results.Json(new { error = "Wrong username or password." }, statusCode: 401);
            }
            failures.TryRemove(name, out _);
            await SignIn(http, user!);
            return Results.Ok(new { username = user!.Username, role = user.Role });
        });

        auth.MapPost("/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        });

        // ---- user management (Admin only) ----
        var users = api.MapGroup("/users").RequireAuthorization("Admin");

        users.MapGet("/", async (LeagueContext db) =>
            await db.Users.OrderBy(u => u.Username).Select(u => new UserView(u.Id, u.Username, u.Role)).ToListAsync());

        users.MapPost("/", async (LeagueContext db, UserInput i) =>
        {
            var name = Norm(i.Username);
            if (Validate(name, i.Password, i.Role ?? Roles.Viewer, true) is { } err) return Results.BadRequest(new { error = err });
            if (await db.Users.AnyAsync(u => u.Username == name)) return Results.BadRequest(new { error = "Username already exists." });
            var u = new User { Username = name, PasswordHash = PasswordHasher.Hash(i.Password!), Role = i.Role ?? Roles.Viewer };
            db.Users.Add(u);
            await db.SaveChangesAsync();
            return Results.Ok(new UserView(u.Id, u.Username, u.Role));
        });

        users.MapPut("/{id:int}", async (HttpContext http, LeagueContext db, int id, UserInput i) =>
        {
            var u = await db.Users.FindAsync(id);
            if (u == null) return Results.NotFound();
            var name = Norm(i.Username);
            if (Validate(name, i.Password, i.Role ?? u.Role, false) is { } err) return Results.BadRequest(new { error = err });
            if (await db.Users.AnyAsync(x => x.Username == name && x.Id != id)) return Results.BadRequest(new { error = "Username already exists." });
            var newRole = i.Role ?? u.Role;
            if (u.Role == Roles.Admin && newRole != Roles.Admin && !await db.Users.AnyAsync(x => x.Role == Roles.Admin && x.Id != id))
                return Results.BadRequest(new { error = "There must be at least one admin." });
            u.Username = name; u.Role = newRole;
            if (!string.IsNullOrEmpty(i.Password)) u.PasswordHash = PasswordHasher.Hash(i.Password);
            await db.SaveChangesAsync();
            return Results.Ok(new UserView(u.Id, u.Username, u.Role));
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
