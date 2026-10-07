using System.Net;
using System.Net.Http.Json;

namespace PuulaakiLiiga.Tests;

/// <summary>Who may do what: Admin everything, Manager league data, Viewer read-only, anonymous nothing.</summary>
public class RoleRulesTests : IDisposable
{
    readonly TestApp app = new();
    public void Dispose() => app.Dispose();

    static readonly object NewTeam = new { name = "Test Team" };
    const string EntityRoutes = "teams,players,coaches,contacts,games,penalties";
    public static IEnumerable<object[]> Routes() => EntityRoutes.Split(',').Select(r => new object[] { r });

    // ---- anonymous ----------------------------------------------------------------
    [Theory]
    [InlineData("GET", "/api/data")]
    [InlineData("GET", "/api/users")]
    [InlineData("POST", "/api/teams")]
    [InlineData("PUT", "/api/teams/1")]
    [InlineData("DELETE", "/api/teams/1")]
    [InlineData("POST", "/api/import")]
    [InlineData("POST", "/api/auth/password")]
    public async Task Anonymous_is_rejected_everywhere(string method, string url)
    {
        await app.SetupAdmin();   // after first-run setup nothing is open any more
        var r = await app.Anonymous().SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(new { }) });
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }

    [Fact]
    public async Task Static_ui_files_stay_public_so_the_login_page_can_load()
    {
        Assert.Equal(HttpStatusCode.OK, (await app.Anonymous().GetAsync("/")).StatusCode);
    }

    // ---- first run -------------------------------------------------------------------
    [Fact]
    public async Task First_run_reports_that_setup_is_needed_and_the_first_user_becomes_admin()
    {
        var anon = app.Anonymous();
        Assert.Contains("needsSetup", await anon.GetStringAsync("/api/auth/me"));

        var r = await anon.PostAsJsonAsync("/api/auth/setup", new { username = "Boss", password = "long-enough-1" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var me = await anon.GetStringAsync("/api/auth/me");
        Assert.Contains("\"role\":\"Admin\"", me);
        Assert.Contains("\"username\":\"boss\"", me);   // names are stored lower-case
    }

    [Fact]
    public async Task Setup_only_works_once()
    {
        await app.SetupAdmin();
        var r = await app.Anonymous().PostAsJsonAsync("/api/auth/setup", new { username = "intruder", password = "long-enough-1" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Setup_rejects_a_short_password_and_a_short_username()
    {
        var anon = app.Anonymous();
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/auth/setup", new { username = "admin", password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/auth/setup", new { username = "ab", password = "long-enough-1" })).StatusCode);
        Assert.Contains("needsSetup", await anon.GetStringAsync("/api/auth/me"));   // nothing was created
    }

    // ---- the role matrix -----------------------------------------------------------------
    [Theory]
    [InlineData("Viewer", false, false)]
    [InlineData("Manager", true, false)]
    [InlineData("Admin", true, true)]
    public async Task Role_permissions(string role, bool canEdit, bool isAdmin)
    {
        var admin = await app.SetupAdmin();
        var user = role == "Admin" ? admin : await app.UserWithRole(admin, role);

        // everyone signed in can read
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/data")).StatusCode);

        // league data: create / update / delete
        var create = await user.PostAsJsonAsync("/api/teams", NewTeam);
        Assert.Equal(canEdit ? HttpStatusCode.Created : HttpStatusCode.Forbidden, create.StatusCode);

        var id = (await (await admin.PostAsJsonAsync("/api/teams", new { name = "Admin Team" })).Content.ReadFromJsonAsync<IdOnly>())!.Id;
        Assert.Equal(canEdit ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await user.PutAsJsonAsync($"/api/teams/{id}", new { name = "Renamed" })).StatusCode);
        Assert.Equal(canEdit ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden, (await user.DeleteAsync($"/api/teams/{id}")).StatusCode);

        // import and user management: Admin only
        Assert.Equal(isAdmin ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/import", new { teams = new[] { new { id = 1, name = "Imported" } } })).StatusCode);
        Assert.Equal(isAdmin ? HttpStatusCode.OK : HttpStatusCode.Forbidden, (await user.GetAsync("/api/users")).StatusCode);
        Assert.Equal(isAdmin ? HttpStatusCode.OK : HttpStatusCode.Forbidden,
            (await user.PostAsJsonAsync("/api/users", new { username = "newcomer", password = "long-enough-1", role = "Viewer" })).StatusCode);
    }

    record IdOnly(int Id);

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task A_viewer_cannot_write_to_any_league_table(string route)
    {
        var viewer = await app.UserWithRole(await app.SetupAdmin(), "Viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/{route}", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync($"/api/{route}/1", new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.DeleteAsync($"/api/{route}/1")).StatusCode);
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task A_manager_is_allowed_to_write_to_every_league_table(string route)
    {
        var manager = await app.UserWithRole(await app.SetupAdmin(), "Manager");
        var r = await manager.PostAsJsonAsync($"/api/{route}", new { name = "x", teamId = 1, playerId = 1, reason = "r", date = "2026-01-01T10:00", homeId = 1, awayId = 2 });
        Assert.Equal(HttpStatusCode.Created, r.StatusCode);
    }

    [Fact]
    public async Task User_data_never_exposes_password_hashes()
    {
        var admin = await app.SetupAdmin();
        var users = (await admin.GetStringAsync("/api/users")).ToLowerInvariant();
        Assert.DoesNotContain("hash", users);
        Assert.DoesNotContain("pbkdf", users);
        Assert.DoesNotContain("users", (await admin.GetStringAsync("/api/data")).ToLowerInvariant());
    }

    // ---- managing users ------------------------------------------------------------------------
    [Fact]
    public async Task Usernames_are_unique_regardless_of_case()
    {
        var admin = await app.SetupAdmin();
        await app.AddUser(admin, "vera", "Viewer");
        var r = await admin.PostAsJsonAsync("/api/users", new { username = "VERA", password = "long-enough-1", role = "Viewer" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Theory]
    [InlineData("Superuser")]
    [InlineData("admin")]   // role names are case-sensitive; only the three known roles exist
    public async Task Unknown_roles_are_rejected(string role)
    {
        var admin = await app.SetupAdmin();
        var r = await admin.PostAsJsonAsync("/api/users", new { username = "someone", password = "long-enough-1", role });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task New_users_need_a_long_enough_password_and_a_valid_email()
    {
        var admin = await app.SetupAdmin();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/users", new { username = "shorty", password = "short", role = "Viewer" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/users", new { username = "mailer", password = "long-enough-1", role = "Viewer", email = "nope" })).StatusCode);
    }

    [Fact]
    public async Task An_admin_cannot_delete_their_own_account()
    {
        var admin = await app.SetupAdmin();
        var me = (await admin.GetFromJsonAsync<TestApp.UserDto[]>("/api/users"))!.Single();
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.DeleteAsync($"/api/users/{me.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/data")).StatusCode);
    }

    [Fact]
    public async Task The_last_admin_cannot_be_demoted_but_one_of_two_can()
    {
        var admin = await app.SetupAdmin();
        var me = (await admin.GetFromJsonAsync<TestApp.UserDto[]>("/api/users"))!.Single();
        var demote = new { username = TestApp.AdminName, role = "Viewer" };
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"/api/users/{me.Id}", demote)).StatusCode);

        var otherId = await app.AddUser(admin, "second-admin", "Admin");
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/users/{me.Id}", demote)).StatusCode);
        Assert.NotEqual(0, otherId);
    }

    // ---- sessions follow the user's current state ---------------------------------------------------
    [Fact]
    public async Task Changing_a_role_ends_that_users_sessions_and_the_new_role_applies_on_next_login()
    {
        var admin = await app.SetupAdmin();
        var id = await app.AddUser(admin, "mike", "Manager");
        var mike = await app.SignInAs("mike", "user-password-1");
        Assert.Equal(HttpStatusCode.Created, (await mike.PostAsJsonAsync("/api/teams", NewTeam)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"/api/users/{id}", new { username = "mike", role = "Viewer" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await mike.GetAsync("/api/data")).StatusCode);   // old session is dead
        var again = await app.SignInAs("mike", "user-password-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await again.PostAsJsonAsync("/api/teams", NewTeam)).StatusCode);   // now read-only
    }

    [Fact]
    public async Task A_role_change_that_changes_nothing_keeps_the_session()
    {
        var admin = await app.SetupAdmin();
        var id = await app.AddUser(admin, "vera", "Viewer");
        var vera = await app.SignInAs("vera", "user-password-1");
        await admin.PutAsJsonAsync($"/api/users/{id}", new { username = "vera", role = "Viewer", email = "vera@example.com" });
        Assert.Equal(HttpStatusCode.OK, (await vera.GetAsync("/api/data")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_user_ends_their_session_immediately()
    {
        var admin = await app.SetupAdmin();
        var id = await app.AddUser(admin, "vera", "Viewer");
        var vera = await app.SignInAs("vera", "user-password-1");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/users/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await vera.GetAsync("/api/data")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Anonymous().PostAsJsonAsync("/api/auth/login", new { username = "vera", password = "user-password-1" })).StatusCode);
    }

    [Fact]
    public async Task An_admin_password_reset_signs_the_user_out_and_the_old_password_stops_working()
    {
        var admin = await app.SetupAdmin();
        var id = await app.AddUser(admin, "vera", "Viewer");
        var vera = await app.SignInAs("vera", "user-password-1");

        await admin.PutAsJsonAsync($"/api/users/{id}", new { username = "vera", role = "Viewer", password = "brand-new-pass-1" });

        Assert.Equal(HttpStatusCode.Unauthorized, (await vera.GetAsync("/api/data")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Anonymous().PostAsJsonAsync("/api/auth/login", new { username = "vera", password = "user-password-1" })).StatusCode);
        await app.SignInAs("vera", "brand-new-pass-1");
    }

    // ---- own password ----------------------------------------------------------------------------------
    [Fact]
    public async Task Changing_your_own_password_needs_the_current_one_and_keeps_only_this_session()
    {
        var admin = await app.SetupAdmin();
        await app.AddUser(admin, "vera", "Viewer");
        var phone = await app.SignInAs("vera", "user-password-1");
        var laptop = await app.SignInAs("vera", "user-password-1");

        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsJsonAsync("/api/auth/password", new { current = "wrong", @new = "another-pass-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PostAsJsonAsync("/api/auth/password", new { current = "user-password-1", @new = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsJsonAsync("/api/auth/password", new { current = "user-password-1", @new = "another-pass-1" })).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/data")).StatusCode);          // the session that changed it stays in
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/api/data")).StatusCode);  // every other one is signed out
    }

    // ---- lockout -----------------------------------------------------------------------------------------
    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_for_the_right_password()
    {
        var admin = await app.SetupAdmin();
        var id = await app.AddUser(admin, "vera", "Viewer");
        var anon = app.Anonymous();
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/auth/login", new { username = "vera", password = "wrong" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/auth/login", new { username = "vera", password = "user-password-1" })).StatusCode);

        // an admin resetting the password lifts the lock
        await admin.PutAsJsonAsync($"/api/users/{id}", new { username = "vera", role = "Viewer", password = "fresh-password-1" });
        await app.SignInAs("vera", "fresh-password-1");
    }

    [Fact]
    public async Task Four_wrong_passwords_do_not_lock_and_a_good_login_resets_the_counter()
    {
        var admin = await app.SetupAdmin();
        await app.AddUser(admin, "vera", "Viewer");
        var anon = app.Anonymous();
        for (var round = 0; round < 2; round++)   // 4 failures, success, 4 failures, success: never locked
        {
            for (var i = 0; i < 4; i++) await anon.PostAsJsonAsync("/api/auth/login", new { username = "vera", password = "wrong" });
            Assert.Equal(HttpStatusCode.OK, (await anon.PostAsJsonAsync("/api/auth/login", new { username = "vera", password = "user-password-1" })).StatusCode);
        }
    }

    [Fact]
    public async Task Unknown_users_and_wrong_passwords_get_the_same_answer()
    {
        var admin = await app.SetupAdmin();
        await app.AddUser(admin, "vera", "Viewer");
        var anon = app.Anonymous();
        var wrongPassword = await anon.PostAsJsonAsync("/api/auth/login", new { username = "vera", password = "wrong" });
        var unknownUser = await anon.PostAsJsonAsync("/api/auth/login", new { username = "nobody", password = "wrong" });
        Assert.Equal(wrongPassword.StatusCode, unknownUser.StatusCode);
        Assert.Equal(await wrongPassword.Content.ReadAsStringAsync(), await unknownUser.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Login_ignores_case_and_surrounding_spaces_in_the_username()
    {
        await app.SetupAdmin();
        var r = await app.Anonymous().PostAsJsonAsync("/api/auth/login", new { username = "  ADMIN ", password = TestApp.AdminPassword });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    // ---- password reset by email (off unless SMTP is configured) ---------------------------------------------
    [Fact]
    public async Task Email_reset_is_disabled_without_smtp_and_never_reveals_accounts()
    {
        var admin = await app.SetupAdmin();
        Assert.Contains("\"resetEnabled\":false", await app.Anonymous().GetStringAsync("/api/auth/config"));

        var anon = app.Anonymous();
        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsJsonAsync("/api/auth/forgot", new { identifier = "admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await anon.PostAsJsonAsync("/api/auth/forgot", new { identifier = "nobody" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await anon.PostAsJsonAsync("/api/auth/reset", new { token = "made-up", password = "long-enough-1" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/data")).StatusCode);   // nothing happened to the admin
    }
}
