package com.servicedesk.notify.overdue;

import java.time.Instant;
import java.util.List;
import java.util.UUID;

/** Exactly the contract's OverdueStep. position is an opaque string. */
public record OverdueStepDto(
        UUID requestId,
        String title,
        UUID requesterId,
        int stepOrder,
        String position,
        List<UUID> assignedUserIds,
        Instant pendingSince,
        Instant dueAt) {
}
