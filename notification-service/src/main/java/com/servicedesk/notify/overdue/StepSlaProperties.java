package com.servicedesk.notify.overdue;

import java.time.Duration;
import java.time.format.DateTimeParseException;
import org.springframework.boot.context.properties.ConfigurationProperties;

/**
 * One SLA for every step: dueAt = pendingSince + stepSla. Env var NOTIFY_STEP_SLA.
 * Bound as a String and parsed with Duration.parse, so only strict ISO-8601 is accepted
 * (Spring's own converter would read "24" as 24 ms and "24h" as PT24H).
 */
@ConfigurationProperties(prefix = "app.notify")
public record StepSlaProperties(String stepSla) {

    public StepSlaProperties {
        String message = "NOTIFY_STEP_SLA must be a positive ISO-8601 duration such as PT24H, got: " + stepSla;
        if (stepSla == null) {
            throw new IllegalArgumentException(message);
        }
        try {
            Duration d = Duration.parse(stepSla);
            if (d.isZero() || d.isNegative()) {
                throw new IllegalArgumentException(message);
            }
        } catch (DateTimeParseException e) {
            // No cause chained: Spring's startup report shows the deepest cause, which would hide this message.
            throw new IllegalArgumentException(message);
        }
    }

    public Duration duration() {
        return Duration.parse(stepSla);
    }
}
