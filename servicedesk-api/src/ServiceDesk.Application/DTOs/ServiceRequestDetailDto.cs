namespace ServiceDesk.Application.DTOs;

/// <summary>
/// GET /api/service-requests/{id}: contract schema ServiceRequestDetail. The request fields plus the requester's
/// name, the audit trail (oldest first) and the stored triage suggestion (or null).
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
string RequesterName,
List<AuditEntryDto> AuditTrail,
TriageSuggestionDto? Triage)
{
    public static ServiceRequestDetailDto From(ServiceRequestDto r, string requesterName, List<AuditEntryDto> auditTrail, TriageSuggestionDto? triage)
    {
        return new ServiceRequestDetailDto(r.Id, r.Title, r.Description, r.Type, r.Status, r.RequesterId, r.DepartmentId, r.CreatedAt, r.UpdatedAt, r.Steps, requesterName, auditTrail, triage);
    }
}
