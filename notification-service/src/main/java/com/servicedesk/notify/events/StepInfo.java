package com.servicedesk.notify.events;

import com.fasterxml.jackson.annotation.JsonIgnoreProperties;
import java.util.List;
import java.util.UUID;

/** A step as carried by currentStep / nextStep. {@code position} is an opaque string. */
@JsonIgnoreProperties(ignoreUnknown = true)
public record StepInfo(int order, String position, List<UUID> approverUserIds) {

    public List<UUID> approversOrEmpty() {
        return approverUserIds == null ? List.of() : approverUserIds;
    }
}
