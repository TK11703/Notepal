SELECT s.id, s.note_id, s.owner_id, s.owner_name, s.owner_email, s.recipient_id,
       s.recipient_email, s.recipient_name, s.permission, s.created_at
  FROM note_shares s WHERE s.owner_id = @OwnerId AND s.note_id = ANY(@NoteIds)
 ORDER BY s.created_at
