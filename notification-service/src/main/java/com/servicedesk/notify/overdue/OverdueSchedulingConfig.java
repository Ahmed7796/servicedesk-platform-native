package com.servicedesk.notify.overdue;

import org.springframework.boot.autoconfigure.condition.ConditionalOnProperty;
import org.springframework.boot.context.properties.EnableConfigurationProperties;
import org.springframework.context.annotation.Configuration;
import org.springframework.scheduling.annotation.EnableScheduling;
import org.springframework.scheduling.annotation.Scheduled;

/**
 * The only place scheduling is switched on. Off when app.notify.overdue-job.enabled=false
 * (the test profiles). The properties bean is required here so a bad interval fails startup.
 * The first run happens at startup, then one interval after each run ends.
 */
@Configuration
@EnableScheduling
@EnableConfigurationProperties(OverdueJobProperties.class)
@ConditionalOnProperty(name = "app.notify.overdue-job.enabled", havingValue = "true", matchIfMissing = true)
public class OverdueSchedulingConfig {

    private final OverdueJob job;

    public OverdueSchedulingConfig(OverdueJob job, OverdueJobProperties properties) {
        this.job = job;
    }

    @Scheduled(fixedDelayString = "${app.notify.overdue-job.interval:PT1M}")
    void tick() {
        job.runOnce();
    }
}
