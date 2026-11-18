package com.servicedesk.notify.health;

import java.util.Map;
import org.springframework.boot.health.actuate.endpoint.HealthEndpoint;
import org.springframework.boot.health.contributor.Status;
import org.springframework.http.ResponseEntity;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RestController;

/**
 * Contract Health schema is exactly {"status": "UP"|"DOWN"}. Actuator's own endpoint adds
 * "groups" and component details, so this controller reuses its aggregated status only.
 */
@RestController
public class HealthController {

    private final HealthEndpoint healthEndpoint;

    public HealthController(HealthEndpoint healthEndpoint) {
        this.healthEndpoint = healthEndpoint;
    }

    @GetMapping("/notify/health")
    public ResponseEntity<Map<String, String>> health() {
        boolean up = Status.UP.equals(healthEndpoint.health().getStatus());
        return ResponseEntity.status(up ? 200 : 503).body(Map.of("status", up ? "UP" : "DOWN"));
    }
}
