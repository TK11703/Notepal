UPDATE pages p
   SET status = @Pending, attempts = 0, error = NULL, updated_at = @Now
  FROM notes n
 WHERE p.id = @PageId AND p.note_id = @NoteId AND n.id = p.note_id
   AND (n.owner_id = @UserId OR EXISTS (
       SELECT 1 FROM note_shares s WHERE s.note_id = n.id AND s.permission = @Contributor
         AND (s.recipient_id = @UserId OR s.recipient_email = @Email)))
   AND p.status NOT IN (@Pending, @Processing)
RETURNING p.id, p.note_id, p.owner_id, p.page_number, p.file_name, p.content_type, p.size_bytes,
          p.status, p.attempts, p.error, p.extracted_text, p.edited_text, p.updated_at;

SELECT EXISTS (
    SELECT 1 FROM pages p JOIN notes n ON n.id = p.note_id
     WHERE p.id = @PageId AND p.note_id = @NoteId
       AND (n.owner_id = @UserId OR EXISTS (
           SELECT 1 FROM note_shares s WHERE s.note_id = n.id AND s.permission = @Contributor
             AND (s.recipient_id = @UserId OR s.recipient_email = @Email))));
