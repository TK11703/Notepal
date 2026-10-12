SELECT count(*)
  FROM pages p
  JOIN notes n ON n.id = p.note_id AND n.owner_id = p.owner_id
 CROSS JOIN websearch_to_tsquery('english', @Term) AS q(query)
 WHERE p.owner_id = @OwnerId AND n.tags @> @Tags
   AND (p.search_vector @@ q.query
        OR n.title_search @@ q.query
        OR n.title ILIKE @Pattern ESCAPE '\'
        OR coalesce(p.edited_text, p.extracted_text, '') ILIKE @Pattern ESCAPE '\');

SELECT p.note_id, n.title, p.id AS page_id, p.page_number,
       ts_headline('english', coalesce(p.edited_text, p.extracted_text, ''), q.query, @HeadlineOptions) AS snippet,
       n.updated_at, n.tags
  FROM pages p
  JOIN notes n ON n.id = p.note_id AND n.owner_id = p.owner_id
 CROSS JOIN websearch_to_tsquery('english', @Term) AS q(query)
 WHERE p.owner_id = @OwnerId AND n.tags @> @Tags
   AND (p.search_vector @@ q.query
        OR n.title_search @@ q.query
        OR n.title ILIKE @Pattern ESCAPE '\'
        OR coalesce(p.edited_text, p.extracted_text, '') ILIKE @Pattern ESCAPE '\')
 ORDER BY ts_rank(p.search_vector, q.query) + ts_rank(n.title_search, q.query) * 2 DESC,
          n.updated_at DESC,
          p.page_number
 LIMIT @Limit OFFSET @Offset
