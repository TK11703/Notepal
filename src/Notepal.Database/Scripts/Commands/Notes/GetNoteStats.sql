SELECT count(DISTINCT n.id)::integer, count(p.id)::integer
  FROM notes n
  LEFT JOIN pages p ON p.note_id = n.id
 WHERE n.owner_id = @OwnerId AND n.tags @> @Tags
