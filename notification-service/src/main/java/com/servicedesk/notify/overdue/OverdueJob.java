package com.servicedesk.notify.overdue;

import com.servicedesk.notify.push.PendingNotice;
import com.servicedesk.notify.push.PushNotifier;
import java.time.Clock;
import java.util.List;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;
import org.springframework.stereotype.Component;

/**
 * Marks steps newly overdue and pushes their approvers, once per step. The claim commits
 * before any push (claim, then push: at most once; a failed push is not retried).
 * {@link OverdueSchedulingConfig} schedules {@link #runOnce()}. Logs carry counts and request ids only.
 */
@Component
public class OverdueJob {

    private static final Logger log = LoggerFactory.getLogger(OverdueJob.class);

    private final OverdueClaimRepository claims;
    private final PushNotifier notifier;
    private final Clock clock;
    private final StepSlaProperties sla;

    public OverdueJob(OverdueClaimRepository claims, PushNotifier notifier, Clock clock, StepSlaProperties sla) {
        this.claims = claims;
        this.notifier = notifier;
        this.clock = clock;
        this.sla = sla;
    }

    /**
     * Must NOT be {@code @Transactional}: the claim has to commit (releasing its row locks)
     * before the first push, so no network call ever happens while rows are locked.
     */
    public void runOnce() {
        List<PendingNotice> claimed = claims.claimDue(clock.instant(), sla.duration());
        claimed.forEach(notifier::notifyOverdue);
        log.info("Overdue job: {} step(s) newly overdue", claimed.size());
    }
}
