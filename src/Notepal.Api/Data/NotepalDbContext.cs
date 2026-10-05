using Microsoft.EntityFrameworkCore;
using Notepal.Api.Auth;
using Notepal.Shared;

namespace Notepal.Api.Data;

public sealed class NotepalDbContext(DbContextOptions<NotepalDbContext> options, ICurrentUser? currentUser = null)
    : DbContext(options)
{
    // Captured per context instance; EF Core parameterises this in the global query filters below so
    // every query issued through a request-scoped context only ever sees the caller's rows.
    private readonly string? _ownerId = currentUser?.UserId;

    // Notes shared with the caller are matched on their object id or, for shares created by e-mail address, on their sign-in address.
    private readonly string? _email = currentUser?.Email;

    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Page> Pages => Set<Page>();
    public DbSet<PageContent> PageContents => Set<PageContent>();
    public DbSet<NoteShare> NoteShares => Set<NoteShare>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Note>(note =>
        {
            note.Property(n => n.OwnerId).HasMaxLength(128);
            note.Property(n => n.Title).HasMaxLength(200);
            note.HasIndex(n => new { n.OwnerId, n.UpdatedAt });
            note.Property(n => n.TitleSearchVector)
                .HasComputedColumnSql("to_tsvector('english', coalesce(\"Title\", ''))", stored: true);
            note.HasIndex(n => n.TitleSearchVector).HasMethod("GIN");
            note.Property(n => n.Tags).HasColumnType("text[]").HasDefaultValueSql("'{}'::text[]");
            note.HasIndex(n => n.Tags).HasMethod("GIN");
            note.HasMany(n => n.Pages).WithOne(p => p.Note).HasForeignKey(p => p.NoteId).OnDelete(DeleteBehavior.Cascade);
            note.HasMany(n => n.Shares).WithOne(s => s.Note).HasForeignKey(s => s.NoteId).OnDelete(DeleteBehavior.Cascade);
            // A note is visible to its owner and to the people it is shared with. Endpoints still check the owner or
            // share permission explicitly for every operation.
            note.HasQueryFilter(n => n.OwnerId == _ownerId ||
                n.Shares.Any(s => (s.RecipientId != null && s.RecipientId == _ownerId) || (_email != null && s.RecipientEmail == _email)));
        });

        modelBuilder.Entity<Page>(page =>
        {
            page.Property(p => p.OwnerId).HasMaxLength(128);
            page.Property(p => p.FileName).HasMaxLength(260);
            page.Property(p => p.ContentType).HasMaxLength(128);
            page.Property(p => p.Error).HasMaxLength(2000);
            page.Ignore(p => p.EffectiveText);
            page.HasIndex(p => new { p.NoteId, p.PageNumber });
            page.HasIndex(p => p.OwnerId);
            page.HasIndex(p => p.Status);
            page.Property(p => p.SearchVector)
                .HasComputedColumnSql("to_tsvector('english', coalesce(\"EditedText\", \"ExtractedText\", ''))", stored: true);
            page.HasIndex(p => p.SearchVector).HasMethod("GIN");
            page.HasOne(p => p.Content).WithOne().HasForeignKey<PageContent>(c => c.PageId).OnDelete(DeleteBehavior.Cascade);
            page.HasQueryFilter(p => p.OwnerId == _ownerId ||
                p.Note.Shares.Any(s => (s.RecipientId != null && s.RecipientId == _ownerId) || (_email != null && s.RecipientEmail == _email)));
        });

        modelBuilder.Entity<NoteShare>(share =>
        {
            share.Property(s => s.OwnerId).HasMaxLength(128);
            share.Property(s => s.OwnerName).HasMaxLength(ShareLimits.MaxDisplayNameLength);
            share.Property(s => s.OwnerEmail).HasMaxLength(ShareLimits.MaxEmailLength);
            share.Property(s => s.RecipientId).HasMaxLength(128);
            share.Property(s => s.RecipientEmail).HasMaxLength(ShareLimits.MaxEmailLength);
            share.Property(s => s.RecipientName).HasMaxLength(ShareLimits.MaxDisplayNameLength);
            share.HasIndex(s => new { s.NoteId, s.RecipientEmail }).IsUnique();
            share.HasIndex(s => s.RecipientId);
            share.HasIndex(s => s.RecipientEmail);
            share.HasIndex(s => s.OwnerId);
            share.HasQueryFilter(s => s.OwnerId == _ownerId ||
                (s.RecipientId != null && s.RecipientId == _ownerId) || (_email != null && s.RecipientEmail == _email));
        });

        modelBuilder.Entity<PageContent>(content =>
        {
            content.HasKey(c => c.PageId);
            content.ToTable("PageContents");
        });
    }
}
