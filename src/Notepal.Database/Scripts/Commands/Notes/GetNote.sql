SELECT n.id, n.owner_id, n.title, n.created_at, n.updated_at, n.tags
  FROM notes n
 WHERE n.id = @NoteId
   AND (n.owner_id = @UserId OR EXISTS (
       SELECT 1 FROM note_shares s WHERE s.note_id = n.id
         AND (s.recipient_id = @UserId OR s.recipient_email = @Email)));

SELECT p.id, p.note_id, p.owner_id, p.page_number, p.file_name, p.content_type, p.size_bytes,
       p.status, p.attempts, p.error, p.extracted_text, p.edited_text, p.updated_at
  FROM pages p
  JOIN notes n ON n.id = p.note_id
 WHERE p.note_id = @NoteId
   AND (n.owner_id = @UserId OR EXISTS (
       SELECT 1 FROM note_shares s WHERE s.note_id = n.id
         AND (s.recipient_id = @UserId OR s.recipient_email = @Email)));

SELECT count(*)::integer FROM note_shares s WHERE s.note_id = @NoteId AND s.owner_id = @UserId;
