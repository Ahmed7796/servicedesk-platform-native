package com.servicedesk.notify.events;

import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.stereotype.Service;
import org.springframework.transaction.annotation.Transactional;

/** Applies one event to the pending_step projection. Safe to call twice with the same event. */
@Service
public class ProjectionHandler {

    private static final Logger log = LoggerFactory.getLogger(ProjectionHandler.class);
    private static final int SUPPORTED_VERSION = 1;

    private final PendingStepRepository repository;

    public ProjectionHandler(PendingStepRepository repository) {
        this.repository = repository;
    }

    @Transactional
    public void handle(EventEnvelope e) {
        if (e.version() != SUPPORTED_VERSION || e.type() == null) {
            log.warn("Skipping event {} of type {} version {}: unsupported", e.eventId(), e.type(), e.version());
            return;
        }
        switch (e.type()) {
            case "request.submitted" -> submitted(e);
            case "request.step_approved" -> stepApproved(e);
            case "request.approved", "request.rejected", "request.cancelled" -> close(e);
            case "request.fulfilled" -> log.debug("Event {} fulfilled: nothing to change", e.eventId());
            default -> log.warn("Skipping event {} with unknown type {}", e.eventId(), e.type());
        }
    }

    private void submitted(EventEnvelope e) {
        StepInfo step = e.payload().currentStep();
        warnIfUnassigned(e, step);
        repository.upsertSubmitted(e.requestId(), e.payload().title(), e.payload().requesterId(), step, e.occurredAt());
    }

    private void stepApproved(EventEnvelope e) {
        StepInfo step = e.payload().nextStep();
        warnIfUnassigned(e, step);
        int changed = repository.moveToStep(e.requestId(), step, e.occurredAt());
        if (changed == 0 && !repository.exists(e.requestId())) {
            log.warn("Event {} step_approved for request {} has no pending row (arrived before submitted?); ignored",
                    e.eventId(), e.requestId());
        }
    }

    private void close(EventEnvelope e) {
        if (repository.close(e.requestId(), e.occurredAt()) == 0 && !repository.exists(e.requestId())) {
            log.warn("Event {} {} for request {} has no pending row; ignored", e.eventId(), e.type(), e.requestId());
        }
    }

    private void warnIfUnassigned(EventEnvelope e, StepInfo step) {
        if (step.approversOrEmpty().isEmpty()) {
            log.warn("Unassigned step: eventId={} requestId={} order={} position={}",
                    e.eventId(), e.requestId(), step.order(), step.position());
        }
    }
}
