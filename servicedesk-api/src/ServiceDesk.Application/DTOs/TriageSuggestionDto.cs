using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Enums;

namespace ServiceDesk.Application.DTOs;

/// <summary>Contract schema TriageSuggestion.</summary>
public sealed record TriageSuggestionDto(Guid RequestId,
RequestType Category,
TriagePriority Priority,
List<ApproverPosition> SuggestedRoute,
string Summary,
double Confidence,
DateTimeOffset SuggestedAt)
{
    public static TriageSuggestionDto FromEntity(TriageSuggestion t)
    {
        return new TriageSuggestionDto(t.RequestId, t.Category, t.Priority, [.. t.SuggestedRoute], t.Summary, t.Confidence, t.SuggestedAt);
    }
}
