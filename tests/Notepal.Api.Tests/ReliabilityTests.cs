using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Azure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Notepal.Api.Ocr;
using Notepal.Api.Processing;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Tests;

public sealed class ReliabilityTests(NotepalApiFactory factory) : IClassFixture<NotepalApiFactory>
{
    private const string InternalDetail = "INTERNAL-DETAIL-not-for-the-client";

    [Theory]
    [InlineData("unexpected", "Text extraction failed. Please try again. Details are in the API logs.", ProcessingStatus.Pending)]
    [InlineData("unconfigured", "OCR is not configured. Contact your administrator.", ProcessingStatus.Failed)]
    [InlineData("unsupported", "Text extraction is not supported for this file.", ProcessingStatus.Failed)]
    [InlineData("service", "Text extraction failed: the OCR service returned an error (429). Details are in the API logs.", ProcessingStatus.Pending)]
    public async Task Extraction_failure_returns_safe_error_and_keeps_original(string kind, string expected, ProcessingStatus status)
    {
        using var app = WithoutWorker();
        using var client = CreateClient(app, Guid.NewGuid().ToString());
        var (user, note) = await CreateNoteAsync(app.Services, client);
        var pages = app.Services.GetRequiredService<PageWorkRepository>();
        var ocr = new DelegateOcr(_ => Task.FromException<string>(kind switch
        {
            "unconfigured" => new OcrUnavailableException(InternalDetail),
            "unsupported" => new NotSupportedException(InternalDetail),
            "service" => new RequestFailedException(429, InternalDetail),
            _ => new InvalidOperationException(InternalDetail),
        }));
        using var stopping = new CancellationTokenSource();
        using var worker = Worker(pages, ocr);

        await worker.ProcessAsync(note.Pages[0].Id, stopping.Token);
        stopping.Cancel();

        using var response = await client.GetAsync($"/api/notes/{note.Id}");
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(InternalDetail, json);
        var result = await response.Content.ReadFromJsonAsync<NoteDto>();
        Assert.NotNull(result);
        Assert.Equal(expected, result.Pages[0].Error);
        Assert.Equal(status, result.Pages[0].Status);
        var original = await app.Services.GetRequiredService<NotesRepository>()
            .GetOriginalAsync(note.Id, note.Pages[0].Id, user, CancellationToken.None);
        Assert.NotNull(original);
        Assert.Equal(TestFiles.Png, original.Data);
    }

    [Fact]
    public async Task Cancellation_during_extraction_leaves_page_recoverable()
    {
        using var app = WithoutWorker();
        using var client = CreateClient(app, Guid.NewGuid().ToString());
        var (_, note) = await CreateNoteAsync(app.Services, client);
        var pages = app.Services.GetRequiredService<PageWorkRepository>();
        using var cancellation = new CancellationTokenSource();
        using var interrupted = Worker(pages, new DelegateOcr(ct =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<string>(ct);
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            interrupted.ProcessAsync(note.Pages[0].Id, cancellation.Token));
        Assert.Contains(note.Pages[0].Id, await pages.GetUnfinishedPageIdsAsync(CancellationToken.None));
        var pending = await client.GetFromJsonAsync<NoteDto>($"/api/notes/{note.Id}");
        Assert.Equal(ProcessingStatus.Processing, pending!.Pages[0].Status);
        Assert.Null(pending.Pages[0].Error);

        using var resumed = Worker(pages, new DelegateOcr(_ => Task.FromResult("Recovered")));
        await resumed.ProcessAsync(note.Pages[0].Id, CancellationToken.None);
        var recovered = await client.GetFromJsonAsync<NoteDto>($"/api/notes/{note.Id}");
        Assert.Equal(ProcessingStatus.Completed, recovered!.Pages[0].Status);
        Assert.Equal("Recovered", recovered.Pages[0].Text);
    }

    [Fact]
    public async Task Finished_extraction_is_persisted_even_when_worker_token_is_canceled()
    {
        using var app = WithoutWorker();
        using var client = CreateClient(app, Guid.NewGuid().ToString());
        var (_, note) = await CreateNoteAsync(app.Services, client);
        using var cancellation = new CancellationTokenSource();
        using var worker = Worker(app.Services.GetRequiredService<PageWorkRepository>(), new DelegateOcr(_ =>
        {
            cancellation.Cancel();
            return Task.FromResult("Finished before shutdown");
        }));

        await worker.ProcessAsync(note.Pages[0].Id, cancellation.Token);
        var result = await client.GetFromJsonAsync<NoteDto>($"/api/notes/{note.Id}");
        Assert.Equal(ProcessingStatus.Completed, result!.Pages[0].Status);
        Assert.Equal("Finished before shutdown", result.Pages[0].Text);
    }

    [Fact]
    public async Task Page_conflicts_use_ProblemDetails_with_the_existing_user_messages()
    {
        using var app = WithoutWorker();
        using var client = CreateClient(app, Guid.NewGuid().ToString());
        var (_, source) = await CreateNoteAsync(app.Services, client);
        var (_, target) = await CreateNoteAsync(app.Services, client);
        var page = source.Pages[0].Id;

        using var deletion = await client.DeleteAsync($"/api/notes/{source.Id}/pages/{page}");
        await AssertConflictAsync(deletion, "A note needs at least one page. Delete the note instead.");

        using var transfer = await client.PostAsJsonAsync($"/api/notes/{source.Id}/pages/{page}/transfer",
            new TransferPageRequest(target.Id));
        await AssertConflictAsync(transfer, "A note needs at least one page. Add another page first, or delete the note instead.");

        await app.Services.GetRequiredService<PageWorkRepository>().TryStartAsync(page, CancellationToken.None);
        using var reprocess = await client.PostAsync($"/api/notes/{source.Id}/pages/{page}/reprocess", null);
        await AssertConflictAsync(reprocess, "The page is already being processed.");

        var (_, multiple) = await CreateNoteAsync(app.Services, client, 2);
        var (_, full) = await CreateNoteAsync(app.Services, client, UploadLimits.MaxFilesPerNote);
        using var fullTarget = await client.PostAsJsonAsync(
            $"/api/notes/{multiple.Id}/pages/{multiple.Pages[0].Id}/transfer", new TransferPageRequest(full.Id));
        await AssertConflictAsync(fullTarget, $"The other note already has the maximum of {UploadLimits.MaxFilesPerNote} pages.");
    }

    [Fact]
    public async Task Production_readiness_and_liveness_are_anonymous_when_database_is_available()
    {
        using var app = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = app.CreateClient();
        using var readiness = await client.GetAsync("/readyz");
        using var liveness = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.OK, readiness.StatusCode);
        Assert.Equal("Healthy", await readiness.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithoutWorker() =>
        factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var registration = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(PageProcessingService));
            services.Remove(registration);
        }));

    private static HttpClient CreateClient(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, string userId)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId);
        client.DefaultRequestHeaders.Add(TestAuthHandler.ScopeHeader, "access_as_user");
        return client;
    }

    private static async Task<(DatabaseUser User, NoteDto Note)> CreateNoteAsync(IServiceProvider services, HttpClient client, int count = 1)
    {
        var user = new DatabaseUser(client.DefaultRequestHeaders.GetValues(TestAuthHandler.UserHeader).Single(), null);
        var note = await services.GetRequiredService<NotesRepository>().CreateNoteAsync(user.UserId, "Reliability", [],
            Enumerable.Range(1, count).Select(i => new NewPage($"{i}.png", "image/png", TestFiles.Png)).ToArray(), CancellationToken.None);
        return (user, note);
    }

    private static PageProcessingService Worker(PageWorkRepository pages, IOcrClient ocr) =>
        new(new ProcessingQueue(), pages, new TextExtractionService(ocr, NullLogger<TextExtractionService>.Instance),
            NullLogger<PageProcessingService>.Instance);

    private static async Task AssertConflictAsync(HttpResponseMessage response, string detail)
    {
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(409, json.RootElement.GetProperty("status").GetInt32());
        Assert.Equal(detail, json.RootElement.GetProperty("detail").GetString());
        Assert.False(json.RootElement.TryGetProperty("message", out _));
    }

    private sealed class DelegateOcr(Func<CancellationToken, Task<string>> extract) : IOcrClient
    {
        public Task<string> ExtractTextAsync(ReadOnlyMemory<byte> image, string contentType, string fileName, CancellationToken cancellationToken) =>
            extract(cancellationToken);
    }
}
