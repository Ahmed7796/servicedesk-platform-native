package com.servicedesk.notify.push;

import java.util.List;
import java.util.UUID;

/** A step that just became pending, and who should be told. An empty list means unassigned. */
public record PendingNotice(UUID requestId, String type, String title, List<UUID> approverUserIds) {}
