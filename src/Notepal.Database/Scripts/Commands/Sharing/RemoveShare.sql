DELETE FROM note_shares s
 WHERE s.id = @ShareId AND s.note_id = @NoteId
   AND (s.owner_id = @UserId OR s.recipient_id = @UserId OR s.recipient_email = @Email)
