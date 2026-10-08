using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentRuntime.Infrastructure.Identity;
using AgentRuntime.IntegrationTests.TestSupport;
using AgentRuntime.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace AgentRuntime.IntegrationTests;

/// <summary>The configured local admin (Auth:LocalAdmin) exists after startup, signs in with a
/// username rather than an email, owns the default organization, is an operator, and isn't
/// recreated or reset on a later start.</summary>
public sealed class LocalAdminTests(LocalAdminTests.Fixture fixture, ITestOutputHelper output) : IClassFixture<LocalAdminTests.Fixture>
{
    public sealed class Fixture : IAsyncLifetime
    {
        public ApiTestHost Host { get; } = new();

        public Task InitializeAsync()
        {
            Host.Settings["Auth:LocalAdmin:Username"] = "admin";
            Host.Settings["Auth:LocalAdmin:Password"] = "admin";
            return Host.InitializeAsync();
        }

        public Task DisposeAsync() => Host.DisposeAsync();
    }

    private ApiTestHost Host => fixture.Host;

    private bool Skip()
    {
        if (Host.Unavailable is null) return false;
        output.WriteLine("SKIPPED: " + Host.Unavailable);
        return true;
    }

    [Fact]
    public async Task The_local_admin_signs_in_with_a_username_and_owns_the_default_organization()
    {
        if (Skip()) return;
        using var client = Host.Factory.CreateClient();

        var wrong = await client.PostAsJsonAsync("/api/auth/login", new { email = "admin", password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "Admin", password = "admin" });
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());

        var me = JsonDocument.Parse(await client.GetStringAsync("/api/auth/me")).RootElement;
        Assert.True(me.GetProperty("authenticated").GetBoolean());
        Assert.Equal(TenantIds.Default, me.GetProperty("tenant_id").GetString());
        Assert.Equal("Owner", me.GetProperty("role").GetString());
        Assert.True(me.GetProperty("user").GetProperty("platform_admin").GetBoolean());
    }

    [Fact]
    public async Task Seeding_again_keeps_the_existing_account()
    {
        if (Skip()) return;
        var identity = Host.Factory.Services.GetRequiredService<IdentityService>();
        var before = await identity.SignInAsync("admin", "admin", CancellationToken.None);
        Assert.True(before.Success, before.Error);

        await identity.EnsureLocalAdminAsync();

        var after = await identity.SignInAsync("admin", "admin", CancellationToken.None);
        Assert.Equal(before.Value!.User.UserId, after.Value!.User.UserId);
        var memberships = await identity.MembershipsAsync(after.Value.User.UserId, CancellationToken.None);
        Assert.Equal(TenantRole.Owner, Assert.Single(memberships).Role);
    }
}
