SELECT count(*)
  FROM notes n WHERE n.owner_id = @OwnerId AND n.tags @> @Tags;

SELECT n.id, n.title, n.created_at, n.updated_at, n.tags,
       coalesce((SELECT array_agg(p.status) FROM pages p WHERE p.note_id = n.id), '{}'::integer[]) AS statuses,
       (SELECT left(coalesce(p.edited_text, p.extracted_text), 240)
          FROM pages p WHERE p.note_id = n.id ORDER BY p.page_number LIMIT 1) AS preview
  FROM notes n WHERE n.owner_id = @OwnerId AND n.tags @> @Tags
 ORDER BY n.updated_at DESC
 LIMIT @Limit OFFSET @Offset
