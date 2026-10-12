WITH removed AS (
    DELETE FROM note_shares s
     WHERE s.note_id = ANY(@NoteIds) AND s.owner_id <> @UserId
       AND (s.recipient_id = @UserId OR s.recipient_email = @Email)
    RETURNING s.note_id
)
SELECT count(DISTINCT note_id)::integer FROM removed
