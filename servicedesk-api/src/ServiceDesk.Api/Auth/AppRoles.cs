using ServiceDesk.Domain.Enums;

namespace ServiceDesk.Api.Auth;

/// <summary>
/// Role names used in the "role" claim. The three human roles are the <see cref="UserRole"/> names.
/// TriageService is a service client, not a user row, so it is deliberately not a UserRole value.
/// </summary>
public static class AppRoles
{
    public const string TriageService = "TriageService";

    // One comma-separated string, because [Authorize(Roles = ...)] needs a compile-time constant.
    public const string Human = $"{nameof(UserRole.Requester)},{nameof(UserRole.Approver)},{nameof(UserRole.Admin)}";

    public static readonly string[] HumanRoles = Human.Split(',');
}

public static class Policies
{
    /// <summary>The only policy (besides the later CanPostTriage) that lets TriageService in. Used by the two GET endpoints.</summary>
    public const string CanReadRequests = "CanReadRequests";
}
