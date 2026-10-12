UPDATE pages SET note_id = @TargetNoteId, page_number = @PageNumber, updated_at = @Now
 WHERE id = @PageId AND note_id = @NoteId;

UPDATE notes SET updated_at = @Now WHERE id = @TargetNoteId;
