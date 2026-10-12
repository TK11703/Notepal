SELECT count(*) FROM notes n
 WHERE n.owner_id <> @UserId AND EXISTS (
     SELECT 1 FROM note_shares s WHERE s.note_id = n.id
       AND (s.recipient_id = @UserId OR s.recipient_email = @Email));

SELECT n.id, n.title, n.created_at, n.updated_at, n.tags,
       coalesce((SELECT array_agg(p.status) FROM pages p WHERE p.note_id = n.id), '{}'::integer[]) AS statuses,
       (SELECT left(coalesce(p.edited_text, p.extracted_text), 240)
          FROM pages p WHERE p.note_id = n.id ORDER BY p.page_number LIMIT 1) AS preview,
       (SELECT bool_or(s.permission = @Contributor) FROM note_shares s WHERE s.note_id = n.id
           AND (s.recipient_id = @UserId OR s.recipient_email = @Email)) AS is_contributor,
       (SELECT coalesce(s.owner_name, s.owner_email) FROM note_shares s WHERE s.note_id = n.id
           AND (s.recipient_id = @UserId OR s.recipient_email = @Email)
           AND coalesce(s.owner_name, s.owner_email) <> '' ORDER BY s.created_at LIMIT 1) AS shared_by,
       (SELECT min(s.created_at) FROM note_shares s WHERE s.note_id = n.id
           AND (s.recipient_id = @UserId OR s.recipient_email = @Email)) AS shared_at
  FROM notes n
 WHERE n.owner_id <> @UserId AND EXISTS (
     SELECT 1 FROM note_shares s WHERE s.note_id = n.id
       AND (s.recipient_id = @UserId OR s.recipient_email = @Email))
 ORDER BY n.updated_at DESC
 LIMIT @Limit OFFSET @Offset
