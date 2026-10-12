using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Tests;

public sealed class RepositorySubstitutionTests
{
    [Fact]
    public async Task Endpoint_uses_replaced_repository_without_a_database_connection()
    {
        var repository = new StubSharesRepository();
        await using var factory = new BoundaryApiFactory();
        await using var application = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ISharesRepository>();
                services.AddSingleton<ISharesRepository>(repository);
            }));
        using var client = application.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "boundary-user");
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, "caller@example.com");
        client.DefaultRequestHeaders.Add(TestAuthHandler.ScopeHeader, "access_as_user");

        using var response = await client.GetAsync("/api/shared/with-me?page=2&pageSize=3");

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResult<SharedNoteDto>>();
        Assert.NotNull(result);
        Assert.Empty(result.Items);
        Assert.Equal(2, result.Page);
        Assert.Equal(3, result.PageSize);
        Assert.Equal(new DatabaseUser("boundary-user", "caller@example.com"), repository.Caller);
        Assert.Equal(1, repository.Calls);
    }

    private sealed class StubSharesRepository : ISharesRepository
    {
        public DatabaseUser? Caller { get; private set; }
        public int Calls { get; private set; }

        public Task<PagedResult<SharedNoteDto>> SharedWithMeAsync(DatabaseUser user, int page, int pageSize, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Caller = user;
            Calls++;
            return Task.FromResult(new PagedResult<SharedNoteDto>([], 0, page, pageSize));
        }

        public Task<List<NoteShareDto>> ListSharesAsync(string ownerId, Guid noteId, CancellationToken ct) => throw new NotSupportedException();
        public Task<(UpsertShareOutcome Outcome, NoteShareDto? Share)> UpsertShareAsync(string ownerId, Guid noteId, ShareDetails details, CancellationToken ct) => throw new NotSupportedException();
        public Task<NoteShareDto?> UpdatePermissionAsync(string ownerId, Guid noteId, Guid shareId, SharePermission permission, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> RemoveShareAsync(Guid noteId, Guid shareId, DatabaseUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> LeaveAsync(IEnumerable<Guid> noteIds, DatabaseUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<PagedResult<SharedNoteDto>> SharedByMeAsync(string ownerId, int page, int pageSize, CancellationToken ct) => throw new NotSupportedException();
    }
}
