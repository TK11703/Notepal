CREATE TABLE IF NOT EXISTS schema_migrations (
    version    text PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now(),
    checksum   text
);
ALTER TABLE schema_migrations ADD COLUMN IF NOT EXISTS checksum text;
