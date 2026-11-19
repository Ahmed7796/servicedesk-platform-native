package com.servicedesk.notify.overdue;

import java.time.Duration;
import java.time.format.DateTimeParseException;
import org.springframework.boot.context.properties.ConfigurationProperties;
import org.springframework.boot.context.properties.bind.DefaultValue;

/**
 * Delay between overdue job runs. Env var NOTIFY_OVERDUE_JOB_INTERVAL, default PT1M.
 * Bound as a String and parsed with Duration.parse: only strict ISO-8601 is accepted.
 */
@ConfigurationProperties(prefix = "app.notify.overdue-job")
public record OverdueJobProperties(@DefaultValue("PT1M") String interval) {

    public OverdueJobProperties {
        String message = "NOTIFY_OVERDUE_JOB_INTERVAL must be a positive ISO-8601 duration such as PT1M, got: " + interval;
        if (interval == null) {
            throw new IllegalArgumentException(message);
        }
        try {
            Duration d = Duration.parse(interval);
            if (d.isZero() || d.isNegative()) {
                throw new IllegalArgumentException(message);
            }
        } catch (DateTimeParseException e) {
            // No cause chained: Spring's startup report shows the deepest cause, which would hide this message.
            throw new IllegalArgumentException(message);
        }
    }

    public Duration duration() {
        return Duration.parse(interval);
    }
}
