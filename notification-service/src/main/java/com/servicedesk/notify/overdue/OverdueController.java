package com.servicedesk.notify.overdue;

import java.util.List;
import org.springframework.security.access.prepost.PreAuthorize;
import org.springframework.security.core.Authentication;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RestController;

@RestController
public class OverdueController {

    private final OverdueService service;

    public OverdueController(OverdueService service) {
        this.service = service;
    }

    @GetMapping("/notify/overdue")
    @PreAuthorize("!hasRole('TriageService')")
    public List<OverdueStepDto> overdue(Authentication auth) {
        return service.overdueFor(auth);
    }
}
