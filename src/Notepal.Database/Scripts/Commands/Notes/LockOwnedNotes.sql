SELECT id FROM notes WHERE id = ANY(@Ids) AND owner_id = @UserId ORDER BY id FOR UPDATE
