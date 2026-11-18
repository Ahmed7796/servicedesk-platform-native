package com.servicedesk.notify.events;

import java.time.Duration;
import java.util.UUID;
import org.springframework.boot.autoconfigure.condition.ConditionalOnProperty;
import org.springframework.data.redis.core.StringRedisTemplate;
import org.springframework.stereotype.Component;

/**
 * Key {@code processed:{eventId}}, set with NX and a 7 day TTL. Seven days is longer than any
 * plausible broker outage or outbox retry; beyond that the occurredAt guard in the
 * projection is the backstop. A Redis error is thrown, never swallowed, so the check is
 * never skipped.
 */
@Component
@ConditionalOnProperty(name = "app.messaging.enabled", havingValue = "true", matchIfMissing = true)
public class RedisProcessedEventStore implements ProcessedEventStore {

    static final Duration TTL = Duration.ofDays(7);

    private final StringRedisTemplate redis;

    public RedisProcessedEventStore(StringRedisTemplate redis) {
        this.redis = redis;
    }

    @Override
    public boolean isProcessed(UUID eventId) {
        return Boolean.TRUE.equals(redis.hasKey(key(eventId)));
    }

    @Override
    public void markProcessed(UUID eventId) {
        redis.opsForValue().setIfAbsent(key(eventId), "1", TTL);
    }

    private static String key(UUID eventId) {
        return "processed:" + eventId;
    }
}
