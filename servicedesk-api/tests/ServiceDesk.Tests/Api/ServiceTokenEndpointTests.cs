using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using NSubstitute;
using ServiceDesk.Api.Auth;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Enums;

namespace ServiceDesk.Tests.Api;

public class ServiceTokenEndpointTests : IClassFixture<ServiceTokenApiFactory>
{
    private readonly ServiceTokenApiFactory _factory;
    private readonly HttpClient _client;

    public ServiceTokenEndpointTests(ServiceTokenApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private Task<HttpResponseMessage> PostServiceToken(string json) => _client.PostAsync("/api/auth/service-token", Json(json));

    private async Task<string> ServiceToken()
    {
        var response = await PostServiceToken($$"""{"clientId":"{{ServiceTokenApiFactory.ClientId}}","clientSecret":"{{ServiceTokenApiFactory.ClientSecret}}"}""");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    // A real user token from the same issuer as login (the user does not have to exist for role checks).
    private string UserToken(UserRole role, TimeProvider? time = null)
    {
        var config = _factory.Services.GetRequiredService<IConfiguration>();
        var user = role switch
        {
            UserRole.Admin => User.CreateAdmin("admin@servicedesk.local", "9001", "hash", "Admin", Guid.NewGuid()),
            UserRole.Approver => User.CreateApprover("a@servicedesk.local", "2001", "hash", "Layla", Guid.NewGuid(), ApproverPosition.DepartmentHead),
            _ => User.CreateRequester("r@servicedesk.local", "1001", "hash", "Omar", Guid.NewGuid()),
        };
        return new JwtTokenService(config, time ?? TimeProvider.System).CreateToken(user).AccessToken;
    }

    private HttpRequestMessage Req(HttpMethod method, string url, string? token, string? body = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = Json(body);
        return request;
    }

    private static readonly string AnyId = Guid.NewGuid().ToString();

    [Fact]
    public async Task ServiceToken_ValidCredentials_Returns200WithTokenResultShape()
    {
        var response = await PostServiceToken($$"""{"clientId":"{{ServiceTokenApiFactory.ClientId}}","clientSecret":"{{ServiceTokenApiFactory.ClientSecret}}"}""");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["accessToken", "expiresAt"], body.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.True(body.GetProperty("expiresAt").TryGetDateTimeOffset(out _));
    }

    [Fact]
    public async Task ServiceToken_Claims_AreSubClientIdAndRoleTriageService_AndExpiryMatchesExpiresAt()
    {
        var response = await PostServiceToken($$"""{"clientId":"{{ServiceTokenApiFactory.ClientId}}","clientSecret":"{{ServiceTokenApiFactory.ClientSecret}}"}""");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var jwt = new JsonWebToken(body.GetProperty("accessToken").GetString());

        Assert.Equal(ServiceTokenApiFactory.ClientId, jwt.GetClaim("sub").Value);
        Assert.Equal(AppRoles.TriageService, jwt.GetClaim("role").Value);
        Assert.Equal("ServiceDesk", jwt.Issuer);
        Assert.Contains("ServiceDesk.Api", jwt.Audiences);
        Assert.DoesNotContain(jwt.Claims, c => c.Type is "email" or "name");
        var expiresAt = body.GetProperty("expiresAt").GetDateTimeOffset();
        Assert.True(Math.Abs((jwt.ValidTo - expiresAt.UtcDateTime).TotalSeconds) < 2);
        Assert.InRange((expiresAt - DateTimeOffset.UtcNow).TotalMinutes, 29, 30.1);
    }

    [Fact]
    public async Task ServiceToken_WrongSecret_Returns401()
    {
        var response = await PostServiceToken($$"""{"clientId":"{{ServiceTokenApiFactory.ClientId}}","clientSecret":"wrong"}""");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ServiceToken_UnknownClient_Returns401_WithSameBodyAsWrongSecret()
    {
        var wrongSecret = await PostServiceToken($$"""{"clientId":"{{ServiceTokenApiFactory.ClientId}}","clientSecret":"wrong"}""");
        var unknown = await PostServiceToken($$"""{"clientId":"nobody","clientSecret":"{{ServiceTokenApiFactory.ClientSecret}}"}""");

        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await Normalised(wrongSecret), await Normalised(unknown));
    }

    // ProblemDetails carries a per-request traceId; everything else must be identical.
    private static async Task<string> Normalised(HttpResponseMessage response)
    {
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        return $"{problem!.Status}|{problem.Title}|{problem.Detail}|{problem.Type}";
    }

    [Theory]
    [InlineData("""{"clientSecret":"x"}""")]
    [InlineData("""{"clientId":"x"}""")]
    [InlineData("""{"clientId":"","clientSecret":"x"}""")]
    [InlineData("""{"clientId":"x","clientSecret":""}""")]
    [InlineData("""{}""")]
    public async Task ServiceToken_EmptyOrMissingField_Returns400(string json)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await PostServiceToken(json)).StatusCode);
    }

    [Fact]
    public async Task ServiceToken_ExtraProperty_Returns400()
    {
        var response = await PostServiceToken($$"""{"clientId":"{{ServiceTokenApiFactory.ClientId}}","clientSecret":"{{ServiceTokenApiFactory.ClientSecret}}","extra":1}""");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ServiceToken_Token_CanListRequests_200()
    {
        _factory.Requests.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ServiceRequest>>([]));

        var response = await _client.SendAsync(Req(HttpMethod.Get, "/api/service-requests", await ServiceToken()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ServiceToken_Token_CanGetOneRequest_ReachesHandler()
    {
        // No such request in the fake repository: 404 comes from the handler, so the policy let the token through.
        var response = await _client.SendAsync(Req(HttpMethod.Get, $"/api/service-requests/{AnyId}", await ServiceToken()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/service-requests", """{"type":"Equipment","title":"t","description":"d"}""")]
    [InlineData("POST", "/api/service-requests/00000000-0000-0000-0000-000000000001/approve", """{"comment":"ok"}""")]
    [InlineData("POST", "/api/service-requests/00000000-0000-0000-0000-000000000001/reject", """{"reason":"no"}""")]
    [InlineData("POST", "/api/service-requests/00000000-0000-0000-0000-000000000001/cancel", null)]
    [InlineData("POST", "/api/service-requests/00000000-0000-0000-0000-000000000001/fulfil", null)]
    [InlineData("GET", "/api/service-requests/pending-for-me", null)]
    public async Task ServiceToken_Token_Gets403_OnRefusedEndpoints(string method, string url, string? body)
    {
        _factory.Requests.ClearReceivedCalls();
        _factory.Users.ClearReceivedCalls();

        var response = await _client.SendAsync(Req(new HttpMethod(method), url, await ServiceToken(), body));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("", await response.Content.ReadAsStringAsync()); // contract: 403 has an empty body
        Assert.Empty(_factory.Requests.ReceivedCalls());
        Assert.Empty(_factory.Users.ReceivedCalls());
    }

    [Theory]
    [InlineData("GET", "/api/service-requests")]
    [InlineData("POST", "/api/service-requests/00000000-0000-0000-0000-000000000001/cancel")]
    public async Task NoToken_Returns401(string method, string url)
    {
        var response = await _client.SendAsync(Req(new HttpMethod(method), url, null));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed class PastClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow.AddHours(-2);
    }

    [Fact]
    public async Task ExpiredToken_Returns401()
    {
        var expired = UserToken(UserRole.Requester, new PastClock());

        var response = await _client.SendAsync(Req(HttpMethod.Get, "/api/service-requests", expired));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TokenSignedWithOtherKey_Returns401()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Issuer"] = "ServiceDesk",
            ["Jwt:Audience"] = "ServiceDesk.Api",
            ["Jwt:ExpiryMinutes"] = "60",
            ["Jwt:Key"] = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()),
        }).Build();
        var foreign = new JwtTokenService(config, TimeProvider.System).CreateServiceToken("x").AccessToken;

        var response = await _client.SendAsync(Req(HttpMethod.Get, "/api/service-requests", foreign));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UserTokens_StillWork()
    {
        _factory.Requests.ListAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<ServiceRequest>>([]));
        var fulfil = $"/api/service-requests/{AnyId}/fulfil";

        // Requester lists.
        Assert.Equal(HttpStatusCode.OK, (await _client.SendAsync(Req(HttpMethod.Get, "/api/service-requests", UserToken(UserRole.Requester)))).StatusCode);
        // Requester and Approver are refused fulfil by the Admin role check (403), Admin reaches the handler (404: no such request).
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(Req(HttpMethod.Post, fulfil, UserToken(UserRole.Requester)))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(Req(HttpMethod.Post, fulfil, UserToken(UserRole.Approver)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(Req(HttpMethod.Post, fulfil, UserToken(UserRole.Admin)))).StatusCode);
        // Approver reaches pending-for-me (404: the user is not in the fake repository, so the handler ran).
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(Req(HttpMethod.Get, "/api/service-requests/pending-for-me", UserToken(UserRole.Approver)))).StatusCode);
        // Submit reaches the handler (404: unknown user).
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(Req(HttpMethod.Post, "/api/service-requests", UserToken(UserRole.Requester), """{"type":"Equipment","title":"t","description":"d"}"""))).StatusCode);
    }

    // Deny by default: every action must carry explicit authorization, and the only actions open to
    // TriageService are the ones that name the CanReadRequests policy.
    [Fact]
    public void Actions_AreProtected_AndOnlyTheReadActionsNameTheReadPolicy()
    {
        var actions = _factory.Services.GetRequiredService<IApiDescriptionGroupCollectionProvider>().ApiDescriptionGroups.Items
            .SelectMany(g => g.Items)
            .Select(i => i.ActionDescriptor)
            .OfType<ControllerActionDescriptor>()
            .ToList();
        Assert.NotEmpty(actions);

        var readPolicy = new List<string>();
        foreach (var a in actions)
        {
            var attrs = a.MethodInfo.GetCustomAttributes<AuthorizeAttribute>(true)
                .Concat(a.ControllerTypeInfo.GetCustomAttributes<AuthorizeAttribute>(true)).ToList();
            var anonymous = a.MethodInfo.GetCustomAttribute<AllowAnonymousAttribute>() is not null
                || a.ControllerTypeInfo.GetCustomAttribute<AllowAnonymousAttribute>() is not null;
            if (anonymous)
            {
                Assert.Equal("AuthController", a.ControllerTypeInfo.Name);
                continue;
            }
            if (attrs.Any(x => x.Policy == Policies.CanReadRequests)) readPolicy.Add(a.ActionName);
            // No class-level attribute: it would be ANDed with the method-level read policy.
            Assert.DoesNotContain(a.ControllerTypeInfo.GetCustomAttributes<AuthorizeAttribute>(true), x => x.Policy == Policies.CanReadRequests);
            Assert.DoesNotContain(a.ControllerTypeInfo.GetCustomAttributes<AuthorizeAttribute>(true), x => x.Roles is null && x.Policy is null);
        }

        Assert.Equal(["GetById", "List"], readPolicy.Order().ToArray());
        // The triage POST is open to TriageService only, through its own policy.
        Assert.Contains(actions, a => a.ActionName == "PostTriage" && a.MethodInfo.GetCustomAttributes<AuthorizeAttribute>(true).Any(x => x.Policy == Policies.CanPostTriage));
    }

    // A future bare [Authorize] uses the default policy, an attribute-less endpoint the fallback: both must refuse TriageService.
    [Fact]
    public async Task DefaultAndFallbackPolicies_RefuseTriageService_AndAcceptHumans()
    {
        var provider = _factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        var authorization = _factory.Services.GetRequiredService<IAuthorizationService>();
        var policies = new[] { await provider.GetDefaultPolicyAsync(), (await provider.GetFallbackPolicyAsync())! };

        foreach (var policy in policies)
        {
            Assert.False((await authorization.AuthorizeAsync(Principal(AppRoles.TriageService), null, policy)).Succeeded);
            foreach (var role in AppRoles.HumanRoles)
                Assert.True((await authorization.AuthorizeAsync(Principal(role), null, policy)).Succeeded);
        }
    }

    private static System.Security.Claims.ClaimsPrincipal Principal(string role) =>
        new(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("role", role)], "test", "sub", "role"));

    [Fact]
    public async Task Health_NeedsNoToken()
    {
        // 200 or 503 depending on the database; the point is that the fallback policy does not turn it into 401.
        var response = await _client.GetAsync("/health");

        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
