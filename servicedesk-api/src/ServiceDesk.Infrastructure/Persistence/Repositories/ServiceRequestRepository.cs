using Microsoft.EntityFrameworkCore;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Enums;

namespace ServiceDesk.Infrastructure.Persistence.Repositories;

public sealed class ServiceRequestRepository(AppDbContext db) : IServiceRequestRepository
{
    public Task<ServiceRequest?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        return db.ServiceRequests
            .Include(r => r.ApprovalSteps)
            .Include(r => r.AuditLogs)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<IReadOnlyList<ServiceRequest>> ListAsync(CancellationToken ct)
    {
        return await db.ServiceRequests
            .AsNoTracking()
            .Include(r => r.ApprovalSteps)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ServiceRequest>> ListPendingCandidatesAsync(User caller, CancellationToken ct)
    {
        // Open requests that are not the caller's own. A superset only: department scope is NOT filtered here.
        var open = db.ServiceRequests
            .AsNoTracking()
            .Include(r => r.ApprovalSteps)
            .Where(r => (r.Status == RequestStatus.Submitted || r.Status == RequestStatus.InReview) && r.RequesterId != caller.Id);

        if (caller.Role == UserRole.Admin)
        {
            // The Admin fallback needs RequesterPosition == step.Position, so a null snapshot can never match.
            open = open.Where(r => r.RequesterPosition != null);
        }
        else
        {
            // Position is compared with the step's stored Position, so an escalated step (stored as GeneralManager) matches the GM.
            var position = caller.Position;
            open = open.Where(r => r.ApprovalSteps.Any(s => s.Decision == StepDecision.Pending && s.Position == position));
        }

        return await open.ToListAsync(ct);
    }

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct)
    {
        return db.ServiceRequests.AsNoTracking().AnyAsync(r => r.Id == id, ct);
    }

    public void Add(ServiceRequest request)
    {
        db.ServiceRequests.Add(request);
    }
}
