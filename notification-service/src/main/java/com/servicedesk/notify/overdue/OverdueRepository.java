package com.servicedesk.notify.overdue;

import java.sql.SQLException;
import java.time.Instant;
import java.time.OffsetDateTime;
import java.time.ZoneOffset;
import java.util.Arrays;
import java.util.List;
import java.util.UUID;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Repository;

@Repository
public class OverdueRepository {

    private static final String COLUMNS = """
            SELECT request_id, title, requester_id, step_order, position, assigned_user_ids, pending_since
            FROM pending_step WHERE closed = false AND pending_since < :cutoff
            """;

    private final JdbcClient jdbc;

    public OverdueRepository(JdbcClient jdbc) {
        this.jdbc = jdbc;
    }

    /** Open steps pending since before {@code cutoff}, oldest first; {@code assignee} null = everyone. */
    public List<OverdueStepDto> findOpenPendingBefore(Instant cutoff, UUID assignee, java.time.Duration sla) {
        String sql = COLUMNS
                + (assignee == null ? "" : " AND CAST(:assignee AS uuid) = ANY(assigned_user_ids)")
                + " ORDER BY pending_since, request_id";
        var spec = jdbc.sql(sql).param("cutoff", cutoff.atOffset(ZoneOffset.UTC));
        if (assignee != null) {
            spec = spec.param("assignee", assignee.toString());
        }
        return spec.query((rs, i) -> {
            Instant since = rs.getObject("pending_since", OffsetDateTime.class).toInstant();
            return new OverdueStepDto(rs.getObject("request_id", UUID.class), rs.getString("title"),
                    rs.getObject("requester_id", UUID.class), rs.getInt("step_order"), rs.getString("position"),
                    uuids(rs.getArray("assigned_user_ids")), since, since.plus(sla));
        }).list();
    }

    private static List<UUID> uuids(java.sql.Array array) throws SQLException {
        return Arrays.stream((Object[]) array.getArray()).map(o -> (UUID) o).toList();
    }
}
