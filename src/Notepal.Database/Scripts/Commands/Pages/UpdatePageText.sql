WITH page AS (
    UPDATE pages p
       SET edited_text = CASE WHEN p.extracted_text = @Text THEN NULL ELSE @Text END,
           updated_at = @Now
      FROM notes n
     WHERE p.id = @PageId AND p.note_id = @NoteId AND n.id = p.note_id
       AND (n.owner_id = @UserId OR EXISTS (
           SELECT 1 FROM note_shares s WHERE s.note_id = n.id AND s.permission = @Contributor
             AND (s.recipient_id = @UserId OR s.recipient_email = @Email)))
    RETURNING p.id, p.note_id, p.owner_id, p.page_number, p.file_name, p.content_type, p.size_bytes,
              p.status, p.attempts, p.error, p.extracted_text, p.edited_text, p.updated_at
), note AS (
    UPDATE notes SET updated_at = @Now
     WHERE id = @NoteId AND EXISTS (SELECT 1 FROM page)
)
SELECT p.id, p.note_id, p.owner_id, p.page_number, p.file_name, p.content_type, p.size_bytes,
       p.status, p.attempts, p.error, p.extracted_text, p.edited_text, p.updated_at FROM page p
