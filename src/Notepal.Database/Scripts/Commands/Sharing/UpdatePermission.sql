UPDATE note_shares s SET permission = @Permission
 WHERE s.id = @ShareId AND s.note_id = @NoteId AND s.owner_id = @OwnerId
RETURNING s.id, s.note_id, s.owner_id, s.owner_name, s.owner_email, s.recipient_id,
          s.recipient_email, s.recipient_name, s.permission, s.created_at
