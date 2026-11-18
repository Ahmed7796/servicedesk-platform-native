package com.servicedesk.notify.security;

import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.test.context.TestConfiguration;
import org.springframework.boot.webmvc.test.autoconfigure.AutoConfigureMockMvc;
import org.springframework.context.annotation.Import;
import org.springframework.security.access.prepost.PreAuthorize;
import org.springframework.test.context.ActiveProfiles;
import org.springframework.test.web.servlet.MockMvc;
import org.springframework.web.bind.annotation.GetMapping;
import org.springframework.web.bind.annotation.RestController;

@SpringBootTest
@AutoConfigureMockMvc
@ActiveProfiles("test")
@Import(SecurityIntegrationTest.ProbeConfig.class)
class SecurityIntegrationTest {

    @TestConfiguration
    @RestController
    static class ProbeConfig {
        @GetMapping("/notify/test-probe")
        String probe() { return "ok"; }

        @GetMapping("/notify/test-users-only")
        @PreAuthorize("!hasRole('TriageService')")
        String usersOnly() { return "ok"; }
    }

    @Autowired MockMvc mvc;

    private static String bearer(String sub, String role) {
        return "Bearer " + TestTokens.valid(sub, role);
    }

    @Test
    void protectedPathWithoutTokenIs401() throws Exception {
        mvc.perform(get("/notify/test-probe")).andExpect(status().isUnauthorized());
    }

    @Test
    void protectedPathWithBadTokenIs401() throws Exception {
        mvc.perform(get("/notify/test-probe").header("Authorization", "Bearer junk"))
                .andExpect(status().isUnauthorized());
    }

    @Test
    void protectedPathWithValidTokenIs200() throws Exception {
        mvc.perform(get("/notify/test-probe").header("Authorization", bearer("u-1", "Requester")))
                .andExpect(status().isOk());
    }

    @Test
    void triageServiceCanBeDenied() throws Exception {
        mvc.perform(get("/notify/test-users-only").header("Authorization", bearer("triage-copilot", "TriageService")))
                .andExpect(status().isForbidden());
        mvc.perform(get("/notify/test-users-only").header("Authorization", bearer("u-1", "Approver")))
                .andExpect(status().isOk());
    }

    @Test
    void actuatorIsDeniedEvenWithValidToken() throws Exception {
        for (String path : new String[] {"/actuator", "/actuator/health"}) {
            mvc.perform(get(path).header("Authorization", bearer("u-1", "Admin")))
                    .andExpect(status().isForbidden());
            mvc.perform(get(path).header("Authorization", bearer("triage-copilot", "TriageService")))
                    .andExpect(status().isForbidden());
        }
    }
}
