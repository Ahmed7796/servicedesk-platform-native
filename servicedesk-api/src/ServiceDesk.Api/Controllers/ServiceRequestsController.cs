using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ServiceDesk.Api.Auth;
using ServiceDesk.Application.DTOs;
using ServiceDesk.Application.Services;

namespace ServiceDesk.Api.Controllers;

[ApiController]
// No class-level [Authorize]: it would be ANDed with the method-level read policy and lock TriageService out of the GETs.
// Methods without an attribute get the fallback policy (human roles only); see AddJwtAuthentication.
[Route("api/service-requests")]
public sealed class ServiceRequestsController(ServiceRequestService service, TriageSuggestionService triageService) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = Policies.CanReadRequests)]
    public async Task<ActionResult<IReadOnlyList<ServiceRequestDto>>> List(CancellationToken ct)
    {
        return Ok(await service.ListAsync(ct));
    }

    // Human roles only; TriageService gets 403.
    [HttpGet("pending-for-me")]
    [Authorize(Roles = AppRoles.Human)]
    public async Task<ActionResult<IReadOnlyList<PendingItemDto>>> ListPendingForMe(CancellationToken ct)
    {
        return Ok(await service.ListPendingForAsync(GetCurrentUserId(), ct));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Policies.CanReadRequests)]
    public async Task<ActionResult<ServiceRequestDetailDto>> GetById(Guid id, CancellationToken ct)
    {
        return Ok(await service.GetDetailAsync(id, ct));
    }

    // TriageService only. Advisory: stores the suggestion and changes nothing else (no audit entry, no event).
    [HttpPost("{id:guid}/triage")]
    [Authorize(Policy = Policies.CanPostTriage)]
    public async Task<ActionResult<TriageSuggestionDto>> PostTriage(Guid id, TriageSuggestionInputDto dto, CancellationToken ct)
    {
        return Ok(await triageService.PostAsync(id, dto, ct));
    }

    [HttpPost]
    public async Task<ActionResult<ServiceRequestDto>> Submit(SubmitServiceRequestDto dto, CancellationToken ct)
    {
        var created = await service.SubmitAsync(GetCurrentUserId(), dto, ct);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPost("{id:guid}/approve")]
    public async Task<ActionResult<ServiceRequestDto>> Approve(Guid id, ApproveDto dto, CancellationToken ct)
    {
        return Ok(await service.ApproveAsync(id, GetCurrentUserId(), dto.Comment, ct));
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<ActionResult<ServiceRequestDto>> Reject(Guid id, RejectDto dto, CancellationToken ct)
    {
        return Ok(await service.RejectAsync(id, GetCurrentUserId(), dto.Reason, ct));
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<ActionResult<ServiceRequestDto>> Cancel(Guid id, CancellationToken ct)
    {
        return Ok(await service.CancelAsync(id, GetCurrentUserId(), ct));
    }

    [HttpPost("{id:guid}/fulfil")]
    [Authorize(Roles = "Admin")]
    public async Task<ActionResult<ServiceRequestDto>> Fulfil(Guid id, CancellationToken ct)
    {
        return Ok(await service.FulfilAsync(id, GetCurrentUserId(), ct));
    }

    // "sub" is kept as-is because AddJwtBearer sets MapInboundClaims = false.
    // Only human tokens get here: a TriageService token has a client id in sub, and the authorization policies
    // refuse it with 403 before any action runs. This parse is not what protects these endpoints.
    private Guid GetCurrentUserId()
    {
        var sub = User.FindFirst("sub")?.Value;

        if (!Guid.TryParse(sub, out var userId))
            throw new UnauthorizedAccessException("The access token has no valid user id (sub claim).");

        return userId;
    }
}
