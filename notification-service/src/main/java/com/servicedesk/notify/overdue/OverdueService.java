package com.servicedesk.notify.overdue;

import java.time.Clock;
import java.util.List;
import java.util.UUID;
import org.springframework.security.core.Authentication;
import org.springframework.stereotype.Service;

/**
 * Overdue is computed at query time: dueAt = pendingSince + SLA, and a step is overdue when
 * now is after dueAt (at exactly dueAt it is not). Changing the SLA applies to every open step.
 */
@Service
public class OverdueService {

    private final OverdueRepository repository;
    private final StepSlaProperties sla;
    private final Clock clock;

    public OverdueService(OverdueRepository repository, StepSlaProperties sla, Clock clock) {
        this.repository = repository;
        this.sla = sla;
        this.clock = clock;
    }

    public List<OverdueStepDto> overdueFor(Authentication auth) {
        var cutoff = clock.instant().minus(sla.duration());
        if (hasRole(auth, "Admin")) {
            return repository.findOpenPendingBefore(cutoff, null, sla.duration());
        }
        if (hasRole(auth, "Approver")) {
            try {
                UUID me = UUID.fromString(auth.getName());
                return repository.findOpenPendingBefore(cutoff, me, sla.duration());
            } catch (IllegalArgumentException notAUuid) {
                return List.of();
            }
        }
        return List.of();
    }

    private static boolean hasRole(Authentication auth, String role) {
        return auth.getAuthorities().stream().anyMatch(a -> a.getAuthority().equals("ROLE_" + role));
    }
}
