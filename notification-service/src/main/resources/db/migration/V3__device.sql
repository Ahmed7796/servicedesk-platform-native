-- One row per Expo push token. A token belongs to one user at a time: registering it again
-- (same or another user) updates the row. roles are the JWT roles at registration; they are
-- how the "unassigned step, tell admins" case finds admin devices (no user directory here).
CREATE TABLE device (
    expo_push_token text        PRIMARY KEY,
    user_id         uuid        NOT NULL,
    platform        varchar(7)  NOT NULL,
    roles           text[]      NOT NULL DEFAULT '{}',
    updated_at      timestamptz NOT NULL
);

CREATE INDEX device_user_idx ON device (user_id);
