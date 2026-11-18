-- Current pending step per request. A projection built only from events.
-- Terminal events set closed = true (a tombstone) instead of deleting, so a late,
-- older event cannot bring a finished request back (guarded by last_event_at).
CREATE TABLE pending_step (
    request_id        uuid        PRIMARY KEY,
    title             text        NOT NULL,
    requester_id      uuid        NOT NULL,
    step_order        int         NOT NULL,
    position          varchar(30) NOT NULL,           -- opaque label, never an enum
    assigned_user_ids uuid[]      NOT NULL DEFAULT '{}', -- empty = unassigned
    pending_since     timestamptz NOT NULL,
    last_event_at     timestamptz NOT NULL,
    closed            boolean     NOT NULL DEFAULT false
);

CREATE INDEX pending_step_open_since_idx ON pending_step (pending_since) WHERE closed = false;
