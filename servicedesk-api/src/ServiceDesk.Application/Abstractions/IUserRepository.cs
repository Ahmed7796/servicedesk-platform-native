using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Enums;
namespace ServiceDesk.Application.Abstractions;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(Guid id, CancellationToken ct);

    // Could be Email or Employee Number, useful for multi-identifier login.
    Task<User?> GetByEmailOrEmployeeNumberAsync(string identifier, CancellationToken ct);

    // A superset of the users who may decide a step: approvers holding the position (any department) plus all Admins.
    // The domain (ServiceRequest.CanDecideCurrentStep) does the real filtering.
    Task<IReadOnlyList<User>> ListApproverCandidatesAsync(ApproverPosition position, CancellationToken ct);

    Task<IReadOnlyList<User>> ListByIdsAsync(IEnumerable<Guid> ids, CancellationToken ct);
}
