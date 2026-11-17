using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Enums;

namespace ServiceDesk.Application.DTOs;

/// <summary>One row of GET /api/service-requests/pending-for-me (contract schema PendingItem).</summary>
public sealed record PendingItemDto(Guid RequestId,
string Title,
RequestType Type,
RequestStatus Status,
Guid RequesterId,
string RequesterName,
Guid DepartmentId,
DateTimeOffset CreatedAt,
ApprovalStepDto CurrentStep,
object? Triage) // Always null until M1-4b replaces this with the real triage DTO. The key must stay in the JSON.
{
    public static PendingItemDto From(ServiceRequest r, string requesterName)
    {
        var s = r.CurrentStep!; // callers only pass requests that passed CanDecideCurrentStep, which needs a pending step

        return new PendingItemDto(r.Id,
        r.Title,
        r.Type,
        r.Status,
        r.RequesterId,
        requesterName,
        r.DepartmentId,
        r.CreatedAt,
        new ApprovalStepDto(s.Order, s.Position, s.Scope, s.Decision, s.DecidedById, s.DecidedAt, s.Comment, s.EscalatedFrom),
        null);
    }
}
