using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ServiceDesk.Api.Auth;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Enums;

namespace ServiceDesk.Tests.Api;

public class TriageEndpointTests : IClassFixture<ServiceTokenApiFactory>
{
    private const string ValidBody = """{"category":"Equipment","priority":"High","suggestedRoute":["ItManager","GeneralManager"],"summary":"Needs IT","confidence":0.8}""";

    private readonly ServiceTokenApiFactory _factory;
    private readonly HttpClient _client;
    private readonly Guid _id = Guid.NewGuid();

    public TriageEndpointTests(ServiceTokenApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        factory.Requests.ClearReceivedCalls();
        factory.Triage.ClearReceivedCalls();
        factory.Outbox.ClearReceivedCalls();
        factory.UnitOfWork.ClearReceivedCalls();
        factory.Requests.ExistsAsync(_id, Arg.Any<CancellationToken>()).Returns(true);
    }

    private async Task<string> ServiceToken()
    {
        var body = $$"""{"clientId":"{{ServiceTokenApiFactory.ClientId}}","clientSecret":"{{ServiceTokenApiFactory.ClientSecret}}"}""";
        var response = await _client.PostAsync("/api/auth/service-token", new StringContent(body, Encoding.UTF8, "application/json"));
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
    }

    private string UserToken(UserRole role)
    {
        var user = role switch
        {
            UserRole.Admin => User.CreateAdmin("admin@servicedesk.local", "9001", "hash", "Admin", Guid.NewGuid()),
            UserRole.Approver => User.CreateApprover("a@servicedesk.local", "2001", "hash", "Layla", Guid.NewGuid(), ApproverPosition.DepartmentHead),
            _ => User.CreateRequester("r@servicedesk.local", "1001", "hash", "Omar", Guid.NewGuid()),
        };
        return new JwtTokenService(_factory.Services.GetRequiredService<IConfiguration>(), TimeProvider.System).CreateToken(user).AccessToken;
    }

    private Task<HttpResponseMessage> Post(string? token, string body, Guid? id = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/service-requests/{id ?? _id}/triage") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return _client.SendAsync(request);
    }

    [Theory]
    [InlineData(UserRole.Requester)]
    [InlineData(UserRole.Approver)]
    [InlineData(UserRole.Admin)]
    public async Task Post_AsUser_Returns403_AndStoresNothing(UserRole role)
    {
        var response = await Post(UserToken(role), "");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await _factory.Triage.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default);
    }

    [Fact]
    public async Task Post_WithoutToken_Returns401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(null, ValidBody)).StatusCode);
    }

    [Fact]
    public async Task Post_WithServiceToken_Returns200_WithTheContractShape()
    {
        var response = await Post(await ServiceToken(), ValidBody);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(["category", "confidence", "priority", "requestId", "suggestedAt", "suggestedRoute", "summary"], body.EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(_id, body.GetProperty("requestId").GetGuid());
        Assert.Equal("High", body.GetProperty("priority").GetString());
        Assert.Equal(["ItManager", "GeneralManager"], body.GetProperty("suggestedRoute").EnumerateArray().Select(e => e.GetString()!).ToArray());
        await _factory.Triage.Received(1).UpsertAsync(Arg.Any<TriageSuggestion>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Post_UnknownRequest_Returns404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Post(await ServiceToken(), ValidBody, Guid.NewGuid())).StatusCode);
    }

    [Fact]
    public async Task Post_ChangesNothingElse_NoRequestLoad_NoAudit_NoOutbox_NoSave()
    {
        await Post(await ServiceToken(), ValidBody);

        await _factory.Requests.DidNotReceiveWithAnyArgs().GetByIdAsync(default, default);
        _factory.Requests.DidNotReceiveWithAnyArgs().Add(default!);
        _factory.Outbox.DidNotReceiveWithAnyArgs().Add(default!);
        await _factory.UnitOfWork.DidNotReceiveWithAnyArgs().SaveChangesAsync(default);
    }

    [Theory]
    [InlineData("""{"category":"Equipment","priority":"High","suggestedRoute":["ItManager"],"summary":"s"}""")] // confidence missing
    [InlineData("""{"category":"Equipment","priority":"High","suggestedRoute":["ItManager"],"summary":"s","confidence":1.5}""")]
    [InlineData("""{"category":"Equipment","priority":"High","suggestedRoute":["ItManager"],"summary":"s","confidence":-1}""")]
    [InlineData("""{"category":"Equipment","priority":"High","suggestedRoute":[],"summary":"s","confidence":0.5}""")]
    [InlineData("""{"category":"Equipment","priority":"High","suggestedRoute":["ItManager","ItManager","ItManager","ItManager","ItManager","ItManager"],"summary":"s","confidence":0.5}""")]
    [InlineData("""{"category":"Equipment","priority":"High","suggestedRoute":["Janitor"],"summary":"s","confidence":0.5}""")]
    [InlineData("""{"category":"Equipment","priority":"Critical","suggestedRoute":["ItManager"],"summary":"s","confidence":0.5}""")]
    [InlineData("""{"category":"Nonsense","priority":"High","suggestedRoute":["ItManager"],"summary":"s","confidence":0.5}""")]
    [InlineData("""{"category":"Equipment","priority":2,"suggestedRoute":["ItManager"],"summary":"s","confidence":0.5}""")] // integer enum
    [InlineData("""{"category":"Equipment","priority":"High","suggestedRoute":["ItManager"],"summary":"","confidence":0.5}""")]
    [InlineData("""{"category":"Equipment","priority":"High","suggestedRoute":["ItManager"],"summary":"s","confidence":0.5,"extra":1}""")]
    [InlineData("""{"category":"Equipment","priority":"High","suggestedRoute":["ItManager"],"summary":"s","confidence":null}""")]
    [InlineData("""{}""")]
    public async Task Post_InvalidBody_Returns400_AndStoresNothing(string json)
    {
        var response = await Post(await ServiceToken(), json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await _factory.Triage.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default);
    }

    [Fact]
    public async Task Detail_ShowsStoredTriage_OrNullBefore()
    {
        var token = await ServiceToken();
        var request = ServiceRequest.Submit(
            User.CreateRequester("r@servicedesk.local", "1001", "hash", "Omar", Guid.NewGuid()), "T", "D",
            new WorkflowTemplate(RequestType.Equipment, "w", [(ApproverPosition.DepartmentHead, ApprovalScope.RequesterDepartment)]), DateTimeOffset.UtcNow);
        _factory.Requests.GetByIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);

        var get = () => _client.SendAsync(new HttpRequestMessage(HttpMethod.Get, $"/api/service-requests/{request.Id}") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } });
        _factory.Triage.GetByRequestIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns((TriageSuggestion?)null);
        var before = await (await get()).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Null, before.GetProperty("triage").ValueKind);

        _factory.Triage.GetByRequestIdAsync(request.Id, Arg.Any<CancellationToken>())
            .Returns(new TriageSuggestion(request.Id, RequestType.Leave, TriagePriority.Urgent, [ApproverPosition.HrManager], "Sum", 0.5, DateTimeOffset.UtcNow));
        var after = await (await get()).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Urgent", after.GetProperty("triage").GetProperty("priority").GetString());
        Assert.Equal(request.Id, after.GetProperty("triage").GetProperty("requestId").GetGuid());
    }
}
