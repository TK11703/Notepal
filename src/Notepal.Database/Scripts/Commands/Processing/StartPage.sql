UPDATE pages p
   SET status = @Processing, attempts = p.attempts + 1, updated_at = @Now
  FROM page_contents c
 WHERE p.id = @PageId AND c.page_id = p.id AND p.status IN (@Pending, @Processing)
RETURNING p.file_name, p.content_type, p.attempts, c.data
