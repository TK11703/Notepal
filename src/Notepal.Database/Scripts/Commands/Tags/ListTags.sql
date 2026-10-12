SELECT t.tag, count(*)::integer
  FROM notes n
 CROSS JOIN unnest(n.tags) AS t(tag)
 WHERE n.owner_id = @OwnerId
 GROUP BY t.tag
