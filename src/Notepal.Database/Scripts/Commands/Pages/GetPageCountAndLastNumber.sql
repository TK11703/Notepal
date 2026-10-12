SELECT count(*)::integer, coalesce(max(page_number), 0)
  FROM pages WHERE note_id = @NoteId
