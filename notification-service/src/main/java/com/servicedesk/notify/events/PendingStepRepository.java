package com.servicedesk.notify.events;

import java.time.Instant;
import java.time.OffsetDateTime;
import java.time.ZoneOffset;
import java.util.List;
import java.util.UUID;
import java.util.stream.Collectors;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Repository;

/**
 * Every write is guarded by {@code last_event_at < :at} (strict), so an older or repeated
 * event never changes a row. That makes each call idempotent on its own.
 */
@Repository
public class PendingStepRepository {

    private final JdbcClient jdbc;

    public PendingStepRepository(JdbcClient jdbc) {
        this.jdbc = jdbc;
    }

    public void upsertSubmitted(UUID requestId, String title, UUID requesterId, StepInfo step, Instant at) {
        jdbc.sql("""
                INSERT INTO pending_step
                    (request_id, title, requester_id, step_order, position, assigned_user_ids,
                     pending_since, last_event_at, closed)
                VALUES (:id, :title, :requester, :stepOrder, :position, CAST(:assigned AS uuid[]), :at, :at, false)
                ON CONFLICT (request_id) DO UPDATE SET
                    title = EXCLUDED.title, requester_id = EXCLUDED.requester_id,
                    step_order = EXCLUDED.step_order, position = EXCLUDED.position,
                    assigned_user_ids = EXCLUDED.assigned_user_ids,
                    pending_since = EXCLUDED.pending_since, last_event_at = EXCLUDED.last_event_at,
                    closed = false
                WHERE pending_step.last_event_at < EXCLUDED.last_event_at
                """)
                .param("id", requestId).param("title", title).param("requester", requesterId)
                .param("stepOrder", step.order()).param("position", step.position())
                .param("assigned", arrayLiteral(step.approversOrEmpty())).param("at", ts(at))
                .update();
    }

    /** Moves an open row to the next step. Returns rows changed (0 = no row, closed, or stale). */
    public int moveToStep(UUID requestId, StepInfo step, Instant at) {
        return jdbc.sql("""
                UPDATE pending_step SET step_order = :stepOrder, position = :position,
                    assigned_user_ids = CAST(:assigned AS uuid[]), pending_since = :at, last_event_at = :at
                WHERE request_id = :id AND closed = false AND last_event_at < :at
                """)
                .param("id", requestId).param("stepOrder", step.order()).param("position", step.position())
                .param("assigned", arrayLiteral(step.approversOrEmpty())).param("at", ts(at))
                .update();
    }

    /** Tombstones the row. Returns rows changed. */
    public int close(UUID requestId, Instant at) {
        return jdbc.sql("""
                UPDATE pending_step SET closed = true, last_event_at = :at
                WHERE request_id = :id AND last_event_at < :at
                """)
                .param("id", requestId).param("at", ts(at)).update();
    }

    public boolean exists(UUID requestId) {
        return jdbc.sql("SELECT count(*) FROM pending_step WHERE request_id = :id")
                .param("id", requestId).query(Long.class).single() > 0;
    }

    private static OffsetDateTime ts(Instant at) {
        return OffsetDateTime.ofInstant(at, ZoneOffset.UTC);
    }

    private static String arrayLiteral(List<UUID> ids) {
        return ids.stream().map(UUID::toString).collect(Collectors.joining(",", "{", "}"));
    }
}
