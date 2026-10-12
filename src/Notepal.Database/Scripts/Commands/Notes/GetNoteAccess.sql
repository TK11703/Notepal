SELECT n.owner_id, s.permission, s.owner_name, s.owner_email
  FROM notes n
  LEFT JOIN note_shares s ON s.note_id = n.id
   AND (s.recipient_id = @UserId OR s.recipient_email = @Email)
 WHERE n.id = @NoteId
 ORDER BY s.created_at
