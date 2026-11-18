using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Enums;

namespace ServiceDesk.Application.DTOs;

/// <summary>Contract schema AuditEntry. AuditLog.Sequence orders the trail but is deliberately not exposed.</summary>
public sealed record AuditEntryDto(AuditAction Action,
RequestStatus? StatusBefore,
RequestStatus StatusAfter,
Guid ActorId,
string ActorName,
string? Details,
DateTimeOffset OccurredAt)
{
    public static AuditEntryDto FromEntity(AuditLog a, string actorName)
    {
        return new AuditEntryDto(a.Action, a.StatusBefore, a.StatusAfter, a.ActorId, actorName, a.Details, a.OccurredAt);
    }
}
