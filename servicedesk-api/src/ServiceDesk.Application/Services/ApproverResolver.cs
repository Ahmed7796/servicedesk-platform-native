using ServiceDesk.Application.Abstractions;
using ServiceDesk.Domain.Entities;

namespace ServiceDesk.Application.Services;

/// <summary>
/// Works out who may decide a request's current step. The repository supplies candidates (a superset);
/// the domain rule <see cref="ServiceRequest.CanDecideCurrentStep"/> decides who is actually eligible.
/// </summary>
public sealed class ApproverResolver(IUserRepository users)
{
    public async Task<IReadOnlyList<Guid>> GetEligibleApproversAsync(ServiceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var step = request.CurrentStep;
        if (step is null)
            return [];

        var candidates = await users.ListApproverCandidatesAsync(step.Position, ct);

        return candidates
            .Where(request.CanDecideCurrentStep)
            .Select(u => u.Id)
            .Distinct() // the event schema requires unique approverUserIds
            .Order()
            .ToList();
    }
}
