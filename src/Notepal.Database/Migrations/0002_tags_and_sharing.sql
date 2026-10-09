ALTER TABLE notes ADD COLUMN tags text[] NOT NULL DEFAULT '{}';

CREATE INDEX ix_notes_tags ON notes USING GIN (tags);

CREATE TABLE note_shares (
    id              uuid PRIMARY KEY,
    note_id         uuid NOT NULL REFERENCES notes (id) ON DELETE CASCADE,
    -- Denormalised owner of the note (the person who shared it).
    owner_id        varchar(128) NOT NULL,
    owner_name      varchar(200),
    owner_email     varchar(254),
    -- Set when the recipient was picked from the directory; otherwise the share is matched on recipient_email.
    recipient_id    varchar(128),
    recipient_email varchar(254) NOT NULL,
    recipient_name  varchar(200),
    permission      integer      NOT NULL DEFAULT 0,
    created_at      timestamptz  NOT NULL,
    CONSTRAINT uq_note_shares_note_recipient_email UNIQUE (note_id, recipient_email)
);

CREATE INDEX ix_note_shares_owner ON note_shares (owner_id);
CREATE INDEX ix_note_shares_recipient_id ON note_shares (recipient_id);
CREATE INDEX ix_note_shares_recipient_email ON note_shares (recipient_email);
