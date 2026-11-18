namespace ServiceDesk.Application.DTOs;

/// <summary>
/// GET /api/service-requests/{id}: the request fields plus the stored triage suggestion (or null).
/// requesterName and auditTrail of the contract's ServiceRequestDetail are added in M1-5.
/// </summary>
public sealed record ServiceRequestDetailDto(Guid Id,
string Title,
string Description,
Domain.Enums.RequestType Type,
Domain.Enums.RequestStatus Status,
Guid RequesterId,
Guid DepartmentId,
DateTimeOffset CreatedAt,
DateTimeOffset UpdatedAt,
List<ApprovalStepDto> Steps,
TriageSuggestionDto? Triage)
{
    public static ServiceRequestDetailDto From(ServiceRequestDto r, TriageSuggestionDto? triage)
    {
        return new ServiceRequestDetailDto(r.Id, r.Title, r.Description, r.Type, r.Status, r.RequesterId, r.DepartmentId, r.CreatedAt, r.UpdatedAt, r.Steps, triage);
    }
}
