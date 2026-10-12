-- Negate first because the unique page-number constraint is checked row by row.
UPDATE pages SET page_number = -page_number WHERE note_id = @NoteId;

UPDATE pages p SET page_number = o.n::integer
  FROM unnest(@Ids) WITH ORDINALITY AS o(id, n)
 WHERE p.id = o.id AND p.note_id = @NoteId;

UPDATE notes SET updated_at = @Now WHERE id = @NoteId;
