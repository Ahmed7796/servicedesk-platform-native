using System.Text.Json.Serialization;
using ServiceDesk.Domain.Enums;

namespace ServiceDesk.Application.DTOs;

/// <summary>
/// Body of POST /api/service-requests/{id}/triage (contract schema TriageSuggestionInput). Every member is nullable
/// so a missing field stays null (a missing confidence must not become 0); TriageSuggestionService rejects the nulls.
/// Unknown JSON properties are refused (400).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TriageSuggestionInputDto(
    RequestType? Category,
    TriagePriority? Priority,
    List<ApproverPosition>? SuggestedRoute,
    string? Summary,
    double? Confidence);
