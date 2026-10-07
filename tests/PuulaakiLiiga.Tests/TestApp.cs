using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace PuulaakiLiiga.Tests;

/// <summary>The real app on a throw-away SQLite file. Every client has its own cookie jar, i.e. its own session.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public const string AdminName = "admin", AdminPassword = "admin-password-1";
    readonly string dbFile = Path.Combine(Path.GetTempPath(), $"puulaakiliiga-test-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Default", $"Data Source={dbFile}");
        builder.UseSetting("Auth:SecureCookies", "false");   // tests talk plain http
        builder.UseSetting("Logging:LogLevel:Default", "Warning");
    }

    public HttpClient Anonymous() => CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true, AllowAutoRedirect = false });

    public async Task<HttpClient> SignInAs(string username, string password)
    {
        var c = Anonymous();
        var r = await c.PostAsJsonAsync("/api/auth/login", new { username, password });
        r.EnsureSuccessStatusCode();
        return c;
    }

    /// <summary>Runs the first-run setup and returns the signed-in admin client.</summary>
    public async Task<HttpClient> SetupAdmin()
    {
        var c = Anonymous();
        (await c.PostAsJsonAsync("/api/auth/setup", new { username = AdminName, password = AdminPassword })).EnsureSuccessStatusCode();
        return c;
    }

    public async Task<int> AddUser(HttpClient admin, string username, string role, string password = "user-password-1")
    {
        var r = await admin.PostAsJsonAsync("/api/users", new { username, password, role });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<UserDto>())!.Id;
    }

    /// <summary>Creates a user with the role and returns a client signed in as them.</summary>
    public async Task<HttpClient> UserWithRole(HttpClient admin, string role)
    {
        var name = role.ToLowerInvariant() + "-user";
        await AddUser(admin, name, role);
        return await SignInAs(name, "user-password-1");
    }

    public record UserDto(int Id, string Username, string Role, string? Email);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { dbFile, dbFile + "-shm", dbFile + "-wal" }) try { File.Delete(f); } catch { }
    }
}
