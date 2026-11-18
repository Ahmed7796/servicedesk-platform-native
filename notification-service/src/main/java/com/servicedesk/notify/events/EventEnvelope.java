package com.servicedesk.notify.events;

import com.fasterxml.jackson.annotation.JsonIgnoreProperties;
import java.time.Instant;
import java.util.UUID;

/** The contract envelope, narrowed to the payload fields this service uses. */
@JsonIgnoreProperties(ignoreUnknown = true)
public record EventEnvelope(
        UUID eventId,
        String type,
        Instant occurredAt,
        UUID requestId,
        UUID actorId,
        Payload payload,
        int version) {

    @JsonIgnoreProperties(ignoreUnknown = true)
    public record Payload(UUID requesterId, String title, StepInfo currentStep, StepInfo nextStep) {}
}
