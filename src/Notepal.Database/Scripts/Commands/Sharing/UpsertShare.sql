INSERT INTO note_shares (id, note_id, owner_id, owner_name, owner_email, recipient_id, recipient_email, recipient_name, permission, created_at)
VALUES (@Id, @NoteId, @OwnerId, @OwnerName, @OwnerEmail, @RecipientId, @RecipientEmail, @RecipientName, @Permission, @CreatedAt)
ON CONFLICT (id) DO UPDATE
   SET owner_name = excluded.owner_name,
       owner_email = excluded.owner_email,
       recipient_id = excluded.recipient_id,
       recipient_email = excluded.recipient_email,
       recipient_name = excluded.recipient_name,
       permission = excluded.permission
