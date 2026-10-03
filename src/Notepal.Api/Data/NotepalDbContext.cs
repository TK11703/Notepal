using Microsoft.EntityFrameworkCore;
using Notepal.Api.Auth;

namespace Notepal.Api.Data;

public sealed class NotepalDbContext(DbContextOptions<NotepalDbContext> options, ICurrentUser? currentUser = null)
    : DbContext(options)
{
    // Captured per context instance; EF Core parameterises this in the global query filters below so
    // every query issued through a request-scoped context only ever sees the caller's rows.
    private readonly string? _ownerId = currentUser?.UserId;

    public DbSet<Note> Notes => Set<Note>();
    public DbSet<Page> Pages => Set<Page>();
    public DbSet<PageContent> PageContents => Set<PageContent>();

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
            note.HasQueryFilter(n => n.OwnerId == _ownerId);
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
            page.HasQueryFilter(p => p.OwnerId == _ownerId);
        });

        modelBuilder.Entity<PageContent>(content =>
        {
            content.HasKey(c => c.PageId);
            content.ToTable("PageContents");
        });
    }
}
