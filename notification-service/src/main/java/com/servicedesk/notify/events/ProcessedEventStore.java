package com.servicedesk.notify.events;

import java.util.UUID;

/** Remembers which event ids were already applied (consumer rule 5). */
public interface ProcessedEventStore {

    boolean isProcessed(UUID eventId);

    /** Call only after the projection update has committed. */
    void markProcessed(UUID eventId);
}
