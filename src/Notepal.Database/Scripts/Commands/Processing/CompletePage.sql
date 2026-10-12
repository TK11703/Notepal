UPDATE pages SET status = @Status, extracted_text = @Text, error = NULL, updated_at = @Now WHERE id = @PageId
