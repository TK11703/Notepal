SELECT p.file_name, p.content_type, c.data
  FROM pages p
  JOIN page_contents c ON c.page_id = p.id
  JOIN notes n ON n.id = p.note_id
 WHERE p.id = @PageId AND p.note_id = @NoteId
   AND (n.owner_id = @UserId OR EXISTS (
       SELECT 1 FROM note_shares s WHERE s.note_id = n.id
         AND (s.recipient_id = @UserId OR s.recipient_email = @Email)))
