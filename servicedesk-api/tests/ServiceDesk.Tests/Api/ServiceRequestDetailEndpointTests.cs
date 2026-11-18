using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using ServiceDesk.Api.Auth;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Enums;

namespace ServiceDesk.Tests.Api;

public class ServiceRequestDetailEndpointTests : IClassFixture<ServiceTokenApiFactory>
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
    private readonly ServiceTokenApiFactory _factory;
    private readonly HttpClient _client;

    public ServiceRequestDetailEndpointTests(ServiceTokenApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    private Task<HttpResponseMessage> Get(Guid id, User? as_)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, $"/api/service-requests/{id}");
        if (as_ is not null)
        {
            var token = new JwtTokenService(_factory.Services.GetRequiredService<IConfiguration>(), TimeProvider.System).CreateToken(as_).AccessToken;
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return _client.SendAsync(message);
    }

    [Fact]
    public async Task Get_ResponseMatchesServiceRequestDetailShape()
    {
        var dept = Guid.NewGuid();
        var opsHead = User.CreateApprover("ops@mail.com", "2001", "hash", "Layla", dept, ApproverPosition.DepartmentHead);
        var workflow = new WorkflowTemplate(RequestType.Equipment, "Equipment", [(ApproverPosition.DepartmentHead, ApprovalScope.RequesterDepartment)]);
        var request = ServiceRequest.Submit(opsHead, "Laptop", "Old one", workflow, Now); // escalated: Submitted + StepEscalated, one timestamp
        ((List<AuditLog>)typeof(ServiceRequest).GetField("_auditLogs", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(request)!).Reverse();
        _factory.Requests.GetByIdAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);
        _factory.Users.ListByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns([opsHead]);

        var response = await Get(request.Id, opsHead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await response.Content.ReadFromJsonAsync<JsonElement>();
        string[] top = ["id", "title", "description", "type", "status", "requesterId", "departmentId", "createdAt", "updatedAt", "steps", "requesterName", "auditTrail", "triage"];
        foreach (var key in top) Assert.True(root.TryGetProperty(key, out _), $"missing top-level key {key}");
        Assert.Equal(JsonValueKind.Null, root.GetProperty("triage").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("type").ValueKind);
        Assert.Equal(JsonValueKind.String, root.GetProperty("status").ValueKind);
        Assert.False(root.TryGetProperty("sequence", out _), "sequence must not be exposed");

        var trail = root.GetProperty("auditTrail").EnumerateArray().ToList();
        Assert.Equal(["Submitted", "StepEscalated"], trail.Select(e => e.GetProperty("action").GetString())); // stored reversed, returned oldest first
        string[] entryKeys = ["action", "statusBefore", "statusAfter", "actorId", "actorName", "details", "occurredAt"];
        foreach (var entry in trail)
        {
            foreach (var key in entryKeys) Assert.True(entry.TryGetProperty(key, out _), $"missing audit key {key}");
            Assert.Equal(JsonValueKind.String, entry.GetProperty("action").ValueKind);
            Assert.Equal(JsonValueKind.String, entry.GetProperty("statusAfter").ValueKind);
            Assert.False(entry.TryGetProperty("sequence", out _), "sequence must not be exposed");
        }
        Assert.Equal(JsonValueKind.Null, trail[0].GetProperty("statusBefore").ValueKind); // Submitted has no status before
        Assert.Equal(JsonValueKind.String, trail[1].GetProperty("statusBefore").ValueKind); // StepEscalated has a status before
        Assert.Equal("Layla", trail[0].GetProperty("actorName").GetString());
    }

    [Fact]
    public async Task Get_Anonymous_Returns401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(Guid.NewGuid(), null)).StatusCode);
    }

    [Fact]
    public async Task Get_UnknownRequest_Returns404()
    {
        var user = User.CreateRequester("r@mail.com", "1001", "hash", "Omar", Guid.NewGuid());
        _factory.Requests.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((ServiceRequest?)null);

        Assert.Equal(HttpStatusCode.NotFound, (await Get(Guid.NewGuid(), user)).StatusCode);
    }
}
