using ServiceDesk.Domain.Enums;

namespace ServiceDesk.Domain.Entities;

/// <summary>
/// The copilot's latest suggestion for one request (advisory only). RequestId is both the primary key and the
/// foreign key, so there is at most one row per request. It lives in its own table: writing it never touches the
/// ServiceRequests row.
/// </summary>
public sealed class TriageSuggestion
{
    public const int SummaryMaxLength = 500;
    public const int RouteMaxLength = 5;

    public Guid RequestId { get; private set; }
    public RequestType Category { get; private set; }
    public TriagePriority Priority { get; private set; }
    public ApproverPosition[] SuggestedRoute { get; private set; } = [];
    public string Summary { get; private set; } = null!;
    public double Confidence { get; private set; }
    public DateTimeOffset SuggestedAt { get; private set; }

    public TriageSuggestion(Guid requestId, RequestType category, TriagePriority priority, ApproverPosition[] suggestedRoute, string summary, double confidence, DateTimeOffset suggestedAt)
    {
        RequestId = requestId;
        Category = category;
        Priority = priority;
        SuggestedRoute = suggestedRoute;
        Summary = summary;
        Confidence = confidence;
        SuggestedAt = suggestedAt;
    }
}
