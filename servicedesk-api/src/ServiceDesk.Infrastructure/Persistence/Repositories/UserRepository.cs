using Microsoft.EntityFrameworkCore;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Enums;

namespace ServiceDesk.Infrastructure.Persistence.Repositories;

public sealed class UserRepository(AppDbContext db) : IUserRepository
{
    public Task<User?> GetByIdAsync(Guid id, CancellationToken ct)
    {
        return db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
    }

    // Emails are stored lower-cased by the User factories, so normalise the same way before comparing.
    public Task<User?> GetByEmailOrEmployeeNumberAsync(string identifier, CancellationToken ct)
    {
        var trimmed = identifier.Trim();
        var email = trimmed.ToLowerInvariant();

        return db.Users.FirstOrDefaultAsync(u => u.Email == email || u.EmployeeNumber == trimmed, ct);
    }

    public async Task<IReadOnlyList<User>> ListApproverCandidatesAsync(ApproverPosition position, CancellationToken ct)
    {
        return await db.Users
            .Where(u => u.Role == UserRole.Admin || (u.Role == UserRole.Approver && u.Position == position))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<User>> ListByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var idList = ids.ToList();

        return await db.Users.AsNoTracking().Where(u => idList.Contains(u.Id)).ToListAsync(ct);
    }
}
