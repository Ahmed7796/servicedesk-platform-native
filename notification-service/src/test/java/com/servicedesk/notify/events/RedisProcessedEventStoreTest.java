package com.servicedesk.notify.events;

import static org.assertj.core.api.Assertions.assertThat;

import java.time.Duration;
import java.util.UUID;
import org.junit.jupiter.api.AfterAll;
import org.junit.jupiter.api.BeforeAll;
import org.junit.jupiter.api.Test;
import org.springframework.data.redis.connection.RedisStandaloneConfiguration;
import org.springframework.data.redis.connection.lettuce.LettuceConnectionFactory;
import org.springframework.data.redis.core.StringRedisTemplate;
import org.testcontainers.containers.GenericContainer;
import org.testcontainers.junit.jupiter.Container;
import org.testcontainers.junit.jupiter.Testcontainers;

/** The store against a real Redis (same image as Compose). */
@Testcontainers
class RedisProcessedEventStoreTest {

    @Container
    static GenericContainer<?> redis = new GenericContainer<>("redis:7.4-alpine").withExposedPorts(6379);

    static LettuceConnectionFactory factory;
    static StringRedisTemplate template;
    RedisProcessedEventStore store = new RedisProcessedEventStore(template);

    @BeforeAll
    static void connect() {
        factory = new LettuceConnectionFactory(
                new RedisStandaloneConfiguration(redis.getHost(), redis.getMappedPort(6379)));
        factory.afterPropertiesSet();
        template = new StringRedisTemplate(factory);
        template.afterPropertiesSet();
    }

    @AfterAll
    static void disconnect() {
        factory.destroy();
    }

    @Test
    void unknownEventIsNotProcessed() {
        assertThat(store.isProcessed(UUID.randomUUID())).isFalse();
    }

    @Test
    void markedEventIsProcessedUnderTheProcessedKeyWithSevenDayTtl() {
        UUID id = UUID.randomUUID();

        store.markProcessed(id);
        store.markProcessed(id); // marking twice is harmless

        assertThat(store.isProcessed(id)).isTrue();
        assertThat(template.hasKey("processed:" + id)).isTrue();
        Long ttl = template.getExpire("processed:" + id);
        assertThat(ttl).isBetween(Duration.ofDays(7).minusMinutes(1).toSeconds(), Duration.ofDays(7).toSeconds());
    }
}
