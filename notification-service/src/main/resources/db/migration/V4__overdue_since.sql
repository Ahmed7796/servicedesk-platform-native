-- Set once by the overdue job when it claims a step; NULL again whenever the step changes,
-- so the next step can become overdue in its turn.
ALTER TABLE pending_step ADD COLUMN overdue_since timestamptz NULL;
