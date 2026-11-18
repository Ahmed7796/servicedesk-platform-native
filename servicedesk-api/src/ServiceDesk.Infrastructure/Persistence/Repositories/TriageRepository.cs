using Microsoft.EntityFrameworkCore;
using ServiceDesk.Application.Abstractions;
using ServiceDesk.Domain.Entities;

namespace ServiceDesk.Infrastructure.Persistence.Repositories;

public sealed class TriageRepository(AppDbContext db) : ITriageRepository
{
    // Known limit: if the request were deleted between the existence check and this statement, the foreign key
    // fails and the caller gets a 500. Requests are never deleted today.
    public async Task UpsertAsync(TriageSuggestion s, CancellationToken ct)
    {
        var route = s.SuggestedRoute.Select(p => p.ToString()).ToArray(); // text[] holds the position names

        // One statement, run in its own implicit transaction: try to INSERT the row; if a row for this RequestId
        // already exists (the primary key), UPDATE every field instead. Two simultaneous first posts cannot collide:
        // PostgreSQL makes one insert and the other an update, and the last one wins. The values are SQL parameters,
        // never concatenated into the text. Names are quoted exactly as EF Core created them.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "TriageSuggestions" ("RequestId", "Category", "Priority", "SuggestedRoute", "Summary", "Confidence", "SuggestedAt")
            VALUES ({s.RequestId}, {s.Category.ToString()}, {s.Priority.ToString()}, {route}, {s.Summary}, {s.Confidence}, {s.SuggestedAt})
            ON CONFLICT ("RequestId") DO UPDATE SET
                "Category" = EXCLUDED."Category",
                "Priority" = EXCLUDED."Priority",
                "SuggestedRoute" = EXCLUDED."SuggestedRoute",
                "Summary" = EXCLUDED."Summary",
                "Confidence" = EXCLUDED."Confidence",
                "SuggestedAt" = EXCLUDED."SuggestedAt"
            """, ct);
    }

    public Task<TriageSuggestion?> GetByRequestIdAsync(Guid requestId, CancellationToken ct)
    {
        return db.TriageSuggestions.AsNoTracking().FirstOrDefaultAsync(t => t.RequestId == requestId, ct);
    }

    public async Task<IReadOnlyList<TriageSuggestion>> ListByRequestIdsAsync(IEnumerable<Guid> requestIds, CancellationToken ct)
    {
        var ids = requestIds.ToList();

        return await db.TriageSuggestions.AsNoTracking().Where(t => ids.Contains(t.RequestId)).ToListAsync(ct);
    }
}
