SELECT n.owner_id FROM notes n
 WHERE n.id = @NoteId
   AND (n.owner_id = @UserId OR EXISTS (
       SELECT 1 FROM note_shares s WHERE s.note_id = n.id AND s.permission = @Contributor
         AND (s.recipient_id = @UserId OR s.recipient_email = @Email)))
 FOR UPDATE
