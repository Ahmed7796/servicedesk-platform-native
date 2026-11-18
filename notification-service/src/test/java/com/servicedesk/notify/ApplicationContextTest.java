package com.servicedesk.notify;

import static org.assertj.core.api.Assertions.assertThat;

import org.junit.jupiter.api.Test;
import org.springframework.beans.factory.annotation.Autowired;
import org.springframework.boot.test.context.SpringBootTest;
import org.springframework.context.ApplicationContext;
import org.springframework.security.oauth2.jwt.JwtDecoder;
import org.springframework.test.context.ActiveProfiles;

@SpringBootTest
@ActiveProfiles("test")
class ApplicationContextTest {

    @Autowired ApplicationContext context;

    @Test
    void contextLoadsWithJwtDecoder() {
        assertThat(context.getBean(JwtDecoder.class)).isNotNull();
    }
}
