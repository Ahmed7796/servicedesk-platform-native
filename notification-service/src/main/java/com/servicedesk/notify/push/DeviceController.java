package com.servicedesk.notify.push;

import java.time.Clock;
import java.util.UUID;
import org.springframework.http.HttpStatus;
import org.springframework.security.access.prepost.PreAuthorize;
import org.springframework.security.core.Authentication;
import org.springframework.security.core.GrantedAuthority;
import org.springframework.web.bind.annotation.PostMapping;
import org.springframework.web.bind.annotation.RequestBody;
import org.springframework.web.bind.annotation.ResponseStatus;
import org.springframework.web.bind.annotation.RestController;
import org.springframework.web.server.ResponseStatusException;
import tools.jackson.databind.JsonNode;

@RestController
public class DeviceController {

    private final DeviceRepository devices;
    private final Clock clock;

    public DeviceController(DeviceRepository devices, Clock clock) {
        this.devices = devices;
        this.clock = clock;
    }

    /** Validation runs inside the method, after the role check, so a service token always gets 403. */
    @PostMapping("/notify/devices")
    @ResponseStatus(HttpStatus.NO_CONTENT)
    @PreAuthorize("!hasRole('TriageService')")
    public void register(@RequestBody JsonNode raw, Authentication auth) {
        String problem = DeviceRegistration.problem(raw);
        if (problem != null) throw new ResponseStatusException(HttpStatus.BAD_REQUEST, problem);
        DeviceRegistration body = DeviceRegistration.of(raw);
        UUID userId;
        try {
            userId = UUID.fromString(auth.getName());
        } catch (IllegalArgumentException e) {
            throw new ResponseStatusException(HttpStatus.BAD_REQUEST, "token subject is not a user id");
        }
        var roles = auth.getAuthorities().stream().map(GrantedAuthority::getAuthority)
                .filter(a -> a.startsWith("ROLE_")).map(a -> a.substring(5)).sorted().toList();
        devices.upsert(body.expoPushToken(), userId, body.platform(), roles, clock.instant());
    }
}
