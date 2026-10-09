CREATE TABLE notes (
    id          uuid PRIMARY KEY,
    owner_id    varchar(128) NOT NULL,
    title       varchar(200) NOT NULL,
    created_at  timestamptz  NOT NULL,
    updated_at  timestamptz  NOT NULL,
    title_search tsvector GENERATED ALWAYS AS (to_tsvector('english', title)) STORED
);

CREATE INDEX ix_notes_owner_updated ON notes (owner_id, updated_at DESC);
CREATE INDEX ix_notes_title_search ON notes USING GIN (title_search);

CREATE TABLE pages (
    id             uuid PRIMARY KEY,
    note_id        uuid NOT NULL REFERENCES notes (id) ON DELETE CASCADE,
    -- Denormalised so every page query can be scoped to the caller without a join.
    owner_id       varchar(128)  NOT NULL,
    page_number    integer       NOT NULL,
    file_name      varchar(260)  NOT NULL,
    content_type   varchar(128)  NOT NULL,
    size_bytes     bigint        NOT NULL,
    status         integer       NOT NULL DEFAULT 0,
    attempts       integer       NOT NULL DEFAULT 0,
    error          varchar(2000),
    extracted_text text,
    edited_text    text,
    updated_at     timestamptz   NOT NULL,
    search_vector  tsvector GENERATED ALWAYS AS (to_tsvector('english', coalesce(edited_text, extracted_text, ''))) STORED,
    CONSTRAINT uq_pages_note_page_number UNIQUE (note_id, page_number)
);

CREATE INDEX ix_pages_owner ON pages (owner_id);
CREATE INDEX ix_pages_unfinished ON pages (updated_at) WHERE status IN (0, 1);
CREATE INDEX ix_pages_search ON pages USING GIN (search_vector);

-- Original bytes live in their own table so listing pages never reads blobs.
CREATE TABLE page_contents (
    page_id uuid PRIMARY KEY REFERENCES pages (id) ON DELETE CASCADE,
    data    bytea NOT NULL
);
