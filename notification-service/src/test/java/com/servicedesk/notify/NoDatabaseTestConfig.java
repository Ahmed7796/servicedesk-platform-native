package com.servicedesk.notify;

import static org.mockito.Mockito.mock;

import org.springframework.context.annotation.Bean;
import org.springframework.context.annotation.Configuration;
import org.springframework.context.annotation.Profile;
import org.springframework.jdbc.core.simple.JdbcClient;

/** The "test" profile has no database; repositories still need a JdbcClient to be wired. */
@Configuration
@Profile("test")
class NoDatabaseTestConfig {

    @Bean
    JdbcClient jdbcClient() {
        return mock(JdbcClient.class);
    }
}
