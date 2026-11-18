package com.servicedesk.notify;

import static org.springframework.test.web.servlet.request.MockMvcRequestBuilders.get;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.content;
import static org.springframework.test.web.servlet.result.MockMvcResultMatchers.status;

import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.health.contributor.Health;
import org.springframework.boot.health.contributor.HealthIndicator;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.boot.test.context.TestConfiguration;
import org.springframework.boot.webmvc.test.autoconfigure.AutoConfigureMockMvc;
import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Import;
import org.springframework.test.context.ActiveProfiles;
import org.springframework.test.web.servlet.MockMvc;

/** Contract: a failing dependency gives 503 and still only {"status":"DOWN"}. */
@SpringBootTest
@AutoConfigureMockMvc
@ActiveProfiles("test")
@Import(HealthDownTest.DownConfig.class)
class HealthDownTest {

    @TestConfiguration
    static class DownConfig {
        @Bean
        HealthIndicator brokenIndicator() {
            return () -> Health.down().withDetail("secret", "hidden").build();
        }
    }

    @Autowired MockMvc mvc;

    @Test
    void downIs503WithOnlyStatus() throws Exception {
        mvc.perform(get("/notify/health"))
                .andExpect(status().isServiceUnavailable())
                .andExpect(content().json("{\"status\":\"DOWN\"}", true));
    }
}
