package com.servicedesk.notify.push;

import java.time.Instant;
import java.time.OffsetDateTime;
import java.time.ZoneOffset;
import java.util.List;
import java.util.UUID;
import java.util.stream.Collectors;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Repository;

@Repository
public class DeviceRepository {

    private final JdbcClient jdbc;

    public DeviceRepository(JdbcClient jdbc) {
        this.jdbc = jdbc;
    }

    /** Insert, or move an existing token to this user with these roles. */
    public void upsert(String token, UUID userId, String platform, List<String> roles, Instant at) {
        jdbc.sql("""
                INSERT INTO device (expo_push_token, user_id, platform, roles, updated_at)
                VALUES (:token, :user, :platform, CAST(:roles AS text[]), :at)
                ON CONFLICT (expo_push_token) DO UPDATE SET
                    user_id = EXCLUDED.user_id, platform = EXCLUDED.platform,
                    roles = EXCLUDED.roles, updated_at = EXCLUDED.updated_at
                """)
                .param("token", token).param("user", userId).param("platform", platform)
                .param("roles", roles.stream().collect(Collectors.joining(",", "{", "}")))
                .param("at", OffsetDateTime.ofInstant(at, ZoneOffset.UTC))
                .update();
    }

    public List<String> tokensForUsers(List<UUID> userIds) {
        if (userIds.isEmpty()) return List.of();
        String arr = userIds.stream().map(UUID::toString).collect(Collectors.joining(",", "{", "}"));
        return jdbc.sql("SELECT expo_push_token FROM device WHERE user_id = ANY(CAST(:ids AS uuid[])) ORDER BY expo_push_token")
                .param("ids", arr).query(String.class).list();
    }

    public List<String> tokensForRole(String role) {
        return jdbc.sql("SELECT expo_push_token FROM device WHERE :role = ANY(roles) ORDER BY expo_push_token")
                .param("role", role).query(String.class).list();
    }

    public void delete(String token) {
        jdbc.sql("DELETE FROM device WHERE expo_push_token = :t").param("t", token).update();
    }
}
