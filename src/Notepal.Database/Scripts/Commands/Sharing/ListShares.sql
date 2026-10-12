SELECT s.id, s.note_id, s.owner_id, s.owner_name, s.owner_email, s.recipient_id,
       s.recipient_email, s.recipient_name, s.permission, s.created_at
  FROM note_shares s WHERE s.note_id = @NoteId AND s.owner_id = @OwnerId
 ORDER BY s.created_at
