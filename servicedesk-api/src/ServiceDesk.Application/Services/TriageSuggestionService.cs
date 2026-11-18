using ServiceDesk.Application.Abstractions;
using ServiceDesk.Application.DTOs;
using ServiceDesk.Domain.Entities;
using ServiceDesk.Domain.Exceptions;

namespace ServiceDesk.Application.Services;

/// <summary>
/// Stores the copilot's suggestion. Advisory only (CLAUDE.md rule 6): it only checks that the request exists. It has
/// no unit of work, no outbox and no audit log, so it cannot change a request, write an audit entry or publish an event.
/// </summary>
public sealed class TriageSuggestionService(IServiceRequestRepository requests, ITriageRepository triage, TimeProvider timeProvider)
{
    public async Task<TriageSuggestionDto> PostAsync(Guid requestId, TriageSuggestionInputDto? input, CancellationToken ct)
    {
        var suggestion = Validate(requestId, input, timeProvider.GetUtcNow()); // ArgumentException -> 400, nothing stored

        if (!await requests.ExistsAsync(requestId, ct))
            throw new NotFoundException($"Request {requestId} not found.");

        await triage.UpsertAsync(suggestion, ct);

        return TriageSuggestionDto.FromEntity(suggestion);
    }

    private static TriageSuggestion Validate(Guid requestId, TriageSuggestionInputDto? input, DateTimeOffset now)
    {
        if (input is null)
            throw new ArgumentException("A triage suggestion is required.");
        if (input.Category is not { } category || !Enum.IsDefined(category))
            throw new ArgumentException("category is required and must be a request type.");
        if (input.Priority is not { } priority || !Enum.IsDefined(priority))
            throw new ArgumentException("priority is required and must be Low, Normal, High or Urgent.");
        if (input.SuggestedRoute is not { Count: >= 1 and <= TriageSuggestion.RouteMaxLength } route || route.Any(p => !Enum.IsDefined(p)))
            throw new ArgumentException($"suggestedRoute must hold 1 to {TriageSuggestion.RouteMaxLength} approver positions.");

        var summary = input.Summary?.Trim();
        if (string.IsNullOrEmpty(summary) || summary.Length > TriageSuggestion.SummaryMaxLength)
            throw new ArgumentException($"summary is required and must be 1 to {TriageSuggestion.SummaryMaxLength} characters.");

        if (input.Confidence is not { } confidence || double.IsNaN(confidence) || confidence < 0 || confidence > 1)
            throw new ArgumentException("confidence is required and must be between 0 and 1.");

        return new TriageSuggestion(requestId, category, priority, [.. route], summary, confidence, now);
    }
}
