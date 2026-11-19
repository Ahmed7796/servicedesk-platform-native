package com.servicedesk.notify.overdue;

import com.servicedesk.notify.push.PendingNotice;
import java.sql.SQLException;
import java.time.Duration;
import java.time.Instant;
import java.time.ZoneOffset;
import java.util.Arrays;
import java.util.List;
import java.util.UUID;
import org.springframework.jdbc.core.simple.JdbcClient;
import org.springframework.stereotype.Repository;

@Repository
public class OverdueClaimRepository {

    /** The {@code type} carried by the notices (and the push data) this repository returns. */
    public static final String TYPE = "step.overdue";

    private final JdbcClient jdbc;

    public OverdueClaimRepository(JdbcClient jdbc) {
        this.jdbc = jdbc;
    }

    /**
     * Marks every open, not-yet-claimed step that is past its due time and returns them.
     * One statement, so two runs (or two instances) can never claim the same step: the
     * second UPDATE waits on the row lock, then re-checks {@code overdue_since IS NULL}.
     * Overdue means strictly after {@code pending_since + sla}, the same rule as GET /notify/overdue.
     */
    public List<PendingNotice> claimDue(Instant now, Duration sla) {
        return jdbc.sql("""
                UPDATE pending_step SET overdue_since = :now
                WHERE closed = false AND overdue_since IS NULL AND pending_since < :cutoff
                RETURNING request_id, title, assigned_user_ids
                """)
                .param("now", now.atOffset(ZoneOffset.UTC))
                .param("cutoff", now.minus(sla).atOffset(ZoneOffset.UTC))
                .query((rs, i) -> new PendingNotice(rs.getObject("request_id", UUID.class), TYPE,
                        rs.getString("title"), uuids(rs.getArray("assigned_user_ids"))))
                .list();
    }

    private static List<UUID> uuids(java.sql.Array array) throws SQLException {
        return Arrays.stream((Object[]) array.getArray()).map(o -> (UUID) o).toList();
    }
}
