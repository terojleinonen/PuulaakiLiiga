using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using TeamManagerClassLibrary;

var builder = WebApplication.CreateBuilder(args);
var connection = builder.Configuration.GetConnectionString("Default") ?? "Data Source=puulaakiliiga.db";
builder.Services.AddDbContext<LeagueContext>(o => o.UseSqlite(connection));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Cookie.Name = "puulaakiliiga.auth";
    o.Cookie.HttpOnly = true;
    o.Cookie.SameSite = SameSiteMode.Strict;
    o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    o.ExpireTimeSpan = TimeSpan.FromHours(8);
    o.SlidingExpiration = true;
    // The UI is a SPA: answer with status codes instead of redirecting to a login page.
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.RequireRole(Roles.Admin))
    .AddPolicy("CanEdit", p => p.RequireRole(Roles.Admin, Roles.Manager));

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LeagueContext>();
    db.Database.EnsureCreated();
    // EnsureCreated skips existing databases, so add the Users table to ones created before logins existed.
    db.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS \"Users\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK_Users\" PRIMARY KEY AUTOINCREMENT, \"Username\" TEXT NOT NULL, \"PasswordHash\" TEXT NOT NULL, \"Role\" TEXT NOT NULL)");
    db.Database.ExecuteSqlRaw("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Users_Username\" ON \"Users\" (\"Username\")");
}

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();

// Every /api route needs a signed-in user; writes additionally need Admin/Manager (see Crud below).
var api = app.MapGroup("/api").RequireAuthorization();
AuthEndpoints.Map(app, api);

// Everything in one call: the UI keeps a local copy and computes standings from it.
api.MapGet("/data", async (LeagueContext db) => new
{
    teams = await db.Teams.AsNoTracking().ToListAsync(),
    players = await db.Players.AsNoTracking().ToListAsync(),
    coaches = await db.Coaches.AsNoTracking().ToListAsync(),
    contacts = await db.Contacts.AsNoTracking().ToListAsync(),
    games = await db.Games.AsNoTracking().ToListAsync(),
    penalties = await db.Penalties.AsNoTracking().ToListAsync(),
});

// Replaces the whole database (used by import and demo data).
api.MapPost("/import", async (LeagueContext db, ImportData d) =>
{
    await using var tx = await db.Database.BeginTransactionAsync();
    db.RemoveRange(db.Penalties); db.RemoveRange(db.Games); db.RemoveRange(db.Players);
    db.RemoveRange(db.Coaches); db.RemoveRange(db.Contacts); db.RemoveRange(db.Teams);
    await db.SaveChangesAsync();
    db.AddRange(d.Teams ?? []); db.AddRange(d.Players ?? []); db.AddRange(d.Coaches ?? []);
    db.AddRange(d.Contacts ?? []); db.AddRange(d.Games ?? []); db.AddRange(d.Penalties ?? []);
    await db.SaveChangesAsync();
    await tx.CommitAsync();
    return Results.NoContent();
}).RequireAuthorization("Admin");

Crud<Team>("teams", async (db, id) =>
{
    await db.Players.Where(p => p.TeamId == id).ExecuteDeleteAsync();
    await db.Games.Where(g => g.HomeId == id || g.AwayId == id).ExecuteDeleteAsync();
    await db.Coaches.Where(c => c.TeamId == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.TeamId, (int?)null));
    await db.Penalties.Where(p => !db.Players.Any(x => x.Id == p.PlayerId)).ExecuteDeleteAsync();
    await db.Penalties.Where(p => p.GameId != null && !db.Games.Any(x => x.Id == p.GameId)).ExecuteUpdateAsync(s => s.SetProperty(p => p.GameId, (int?)null));
});
Crud<Player>("players", async (db, id) => await db.Penalties.Where(p => p.PlayerId == id).ExecuteDeleteAsync());
Crud<Coach>("coaches", async (db, id) => await db.Teams.Where(t => t.CoachId == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.CoachId, (int?)null)));
Crud<Contact>("contacts", async (db, id) => await db.Teams.Where(t => t.ContactId == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.ContactId, (int?)null)));
Crud<Game>("games", async (db, id) => await db.Penalties.Where(p => p.GameId == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.GameId, (int?)null)));
Crud<Penalty>("penalties", (_, _) => Task.CompletedTask);

app.Run();

// POST/PUT/DELETE for one entity type. `cleanup` runs before delete to keep references consistent.
void Crud<T>(string route, Func<LeagueContext, int, Task> cleanup) where T : Entity
{
    var g = api.MapGroup("/" + route);
    g.MapPost("/", async (LeagueContext db, T e) =>
    {
        e.Id = 0;
        db.Add(e);
        await db.SaveChangesAsync();
        return Results.Created($"/api/{route}/{e.Id}", e);
    }).RequireAuthorization("CanEdit");
    g.MapPut("/{id:int}", async (LeagueContext db, int id, T e) =>
    {
        if (!await db.Set<T>().AnyAsync(x => x.Id == id)) return Results.NotFound();
        e.Id = id;
        db.Update(e);
        await db.SaveChangesAsync();
        return Results.Ok(e);
    }).RequireAuthorization("CanEdit");
    g.MapDelete("/{id:int}", async (LeagueContext db, int id) =>
    {
        var n = await db.Set<T>().Where(x => x.Id == id).ExecuteDeleteAsync();
        if (n == 0) return Results.NotFound();
        await cleanup(db, id);
        return Results.NoContent();
    }).RequireAuthorization("CanEdit");
}

record ImportData(List<Team>? Teams, List<Player>? Players, List<Coach>? Coaches, List<Contact>? Contacts, List<Game>? Games, List<Penalty>? Penalties);
