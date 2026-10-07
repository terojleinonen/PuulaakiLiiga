using Microsoft.EntityFrameworkCore;

namespace TeamManagerClassLibrary;

public abstract class Entity { public int Id { get; set; } }

public class Team : Entity { public string Name { get; set; } = ""; public int? CoachId { get; set; } public int? ContactId { get; set; } }
public class Player : Entity { public string Name { get; set; } = ""; public int TeamId { get; set; } public int? Number { get; set; } public string? Position { get; set; } }
public class Coach : Entity { public string Name { get; set; } = ""; public int? TeamId { get; set; } }
public class Contact : Entity { public string Name { get; set; } = ""; public string? Phone { get; set; } public string? Email { get; set; } }
public class Game : Entity
{
    public string Date { get; set; } = "";
    public int HomeId { get; set; }
    public int AwayId { get; set; }
    public int? HomeScore { get; set; }
    public int? AwayScore { get; set; }
    public string? Field { get; set; }
    public string? Notes { get; set; }
}
public class Penalty : Entity { public int PlayerId { get; set; } public int? GameId { get; set; } public int? Minutes { get; set; } public string Reason { get; set; } = ""; }

public static class Roles
{
    public const string Admin = "Admin", Manager = "Manager", Viewer = "Viewer";
    public static readonly string[] All = [Admin, Manager, Viewer];
}
public class User : Entity { public string Username { get; set; } = ""; public string PasswordHash { get; set; } = ""; public string Role { get; set; } = Roles.Viewer; }

public class LeagueContext(DbContextOptions<LeagueContext> options) : DbContext(options)
{
    public DbSet<Team> Teams => Set<Team>();
    public DbSet<Player> Players => Set<Player>();
    public DbSet<Coach> Coaches => Set<Coach>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Game> Games => Set<Game>();
    public DbSet<Penalty> Penalties => Set<Penalty>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder b) => b.Entity<User>().HasIndex(u => u.Username).IsUnique();
}
