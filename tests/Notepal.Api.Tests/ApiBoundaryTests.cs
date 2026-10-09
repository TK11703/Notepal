using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Notepal.Api.Processing;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Tests;

public sealed class ApiBoundaryTests(BoundaryApiFactory factory) : IClassFixture<BoundaryApiFactory>
{
    private const string NoteId = "00000000-0000-0000-0000-000000000010";
    private const string PageId = "00000000-0000-0000-0000-000000000020";
    private const string ShareId = "00000000-0000-0000-0000-000000000030";

    public static IEnumerable<object[]> InvalidRequests()
    {
        var note = $"/api/notes/{NoteId}";
        var page = $"{note}/pages/{PageId}";
        yield return ["PUT", note, """{"title":null}"""];
        yield return ["PUT", note, """{"title":""}"""];
        yield return ["PUT", note, """{"title":"   "}"""];
        yield return ["PUT", note, JsonSerializer.Serialize(new UpdateNoteRequest(new string('x', NoteLimits.MaxTitleLength + 1)))];
        yield return ["PUT", $"{page}/text", """{"text":null}"""];
        yield return ["PUT", $"{page}/text", JsonSerializer.Serialize(new UpdatePageTextRequest(new string('x', NoteLimits.MaxTextLength + 1)))];
        yield return ["PUT", $"{page}/position", """{"pageNumber":0}"""];
        yield return ["PUT", $"{page}/position", JsonSerializer.Serialize(new MovePageRequest(UploadLimits.MaxFilesPerNote + 1))];
        yield return ["POST", $"{page}/transfer", """{"targetNoteId":"00000000-0000-0000-0000-000000000000"}"""];
        yield return ["POST", $"{page}/transfer", JsonSerializer.Serialize(new TransferPageRequest(Guid.Parse(NoteId)))];
        yield return ["PUT", $"{note}/tags", """{"tags":null}"""];
        yield return ["PUT", $"{note}/tags", JsonSerializer.Serialize(new UpdateNoteTagsRequest(
            Enumerable.Range(0, TagLimits.MaxTagsPerNote + 1).Select(i => $"tag-{i}").ToArray()))];
        yield return ["POST", $"{note}/shares", """{"email":null}"""];
        yield return ["POST", $"{note}/shares", """{"email":"invalid"}"""];
        yield return ["POST", $"{note}/shares", """{"email":"person@example.com","permission":99}"""];
        yield return ["POST", $"{note}/shares", JsonSerializer.Serialize(new AddNoteShareRequest("person@example.com", UserId: new string('x', 129)))];
        yield return ["PUT", $"{note}/shares/{ShareId}", """{"permission":99}"""];
        yield return ["POST", "/api/shared/with-me/leave", """{"noteIds":null}"""];
        yield return ["POST", "/api/shared/with-me/leave", """{"noteIds":[]}"""];
        yield return ["POST", "/api/shared/with-me/leave", JsonSerializer.Serialize(new LeaveSharedNotesRequest(
            Enumerable.Range(0, ShareLimits.MaxNotesPerLeave + 1).Select(_ => Guid.NewGuid()).ToArray()))];
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task Invalid_arguments_return_validation_problems_without_database_access(string method, string route, string json)
    {
        using var client = factory.CreateAuthenticatedClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), route)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request);
        await AssertValidationProblemAsync(response);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_uploads_are_rejected_before_database_access(bool append)
    {
        using var client = factory.CreateAuthenticatedClient();
        var route = append ? $"/api/notes/{NoteId}/pages" : "/api/notes";

        using var empty = new MultipartFormDataContent();
        empty.Add(new StringContent("Empty"), "title");
        using var emptyResponse = await client.PostAsync(route, empty);
        await AssertValidationProblemAsync(emptyResponse);

        using var invalid = NotesApiTests.Files(("fake.png", "not an image"u8.ToArray()));
        using var invalidResponse = await client.PostAsync(route, invalid);
        await AssertValidationProblemAsync(invalidResponse);

        using var tooMany = NotesApiTests.Files(Enumerable.Range(0, UploadLimits.MaxFilesPerNote + 1)
            .Select(i => ($"{i}.png", TestFiles.Png)).ToArray());
        using var tooManyResponse = await client.PostAsync(route, tooMany);
        await AssertValidationProblemAsync(tooManyResponse);
    }

    [Fact]
    public void Every_API_route_is_a_protected_feature_slice_with_contract_response_metadata()
    {
        using var client = factory.CreateAuthenticatedClient();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true).ToList();
        Assert.Equal(23, endpoints.Count);

        var binaryRoute = $"/api/notes/{{noteId:guid}}/pages/{{pageId:guid}}/original";
        foreach (var endpoint in endpoints)
        {
            var method = endpoint.Metadata.GetMetadata<System.Reflection.MethodInfo>();
            Assert.NotNull(method);
            Assert.StartsWith("Notepal.Api.Features.", method.DeclaringType?.Namespace);
            var endpointType = method.DeclaringType!;
            Assert.EndsWith("Endpoint", endpointType.Name);
            var operation = endpointType.Name[..^"Endpoint".Length];
            var namespaceParts = endpointType.Namespace!.Split('.');
            Assert.Equal(5, namespaceParts.Length);
            Assert.Equal(operation, namespaceParts[^1]);
            Assert.Contains(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(), policy => policy.Policy == "NotesUser");
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
            Assert.Contains(method.GetParameters(), parameter => parameter.ParameterType == typeof(CancellationToken));

            if (endpoint.RoutePattern.RawText == binaryRoute)
            {
                continue;
            }

            var responses = endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>();
            Assert.NotEmpty(responses);
            foreach (var response in responses.Where(response => response.Type is not null && response.Type != typeof(void)))
            {
                if (response.StatusCode >= 400)
                {
                    Assert.Equal(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), response.Type);
                }
                else
                {
                    AssertContractType(response.Type!);
                }
            }
        }
    }

    [Fact]
    public async Task Production_readiness_fails_without_database_but_liveness_remains_healthy()
    {
        using var production = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = production.CreateClient();
        using var readiness = await client.GetAsync("/readyz");
        using var liveness = await client.GetAsync("/healthz");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        Assert.Equal("Unhealthy", await readiness.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, liveness.StatusCode);
    }

    [Fact]
    public async Task OpenAPI_describes_all_feature_operations_with_contract_schemas()
    {
        using var development = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));
        using var client = development.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var paths = json.RootElement.GetProperty("paths");
        var operations = paths.EnumerateObject().SelectMany(path => path.Value.EnumerateObject())
            .Where(operation => operation.Name is "get" or "post" or "put" or "delete");
        Assert.Equal(23, operations.Count());
        var schema = paths.GetProperty("/api/notes/{noteId}").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("content")
            .GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString();
        Assert.Equal("#/components/schemas/NoteDto", schema);
        var schemas = json.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("NoteShareDto", out _));
        Assert.False(schemas.TryGetProperty("NoteShare", out _));
        Assert.False(schemas.TryGetProperty("Page", out _));
    }

    [Fact]
    public void Contracts_and_database_rows_cannot_expose_database_entities_as_API_contracts()
    {
        var contracts = typeof(NoteDto).Assembly;
        Assert.Equal("Notepal.Contracts", contracts.GetName().Name);
        Assert.DoesNotContain(contracts.GetReferencedAssemblies(),
            reference => reference.Name is "Notepal.Database" or "Notepal.Api" or "Notepal.Web");

        var database = typeof(NotesRepository).Assembly;
        foreach (var name in new[] { "Note", "Page", "NoteShare", "PageContent" })
        {
            var type = database.GetType($"Notepal.Database.{name}", throwOnError: true);
            Assert.NotNull(type);
            Assert.False(type.IsPublic);
        }
    }

    private static void AssertContractType(Type type)
    {
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                AssertContractType(argument);
            }
        }
        else
        {
            Assert.Equal(typeof(NoteDto).Assembly, type.Assembly);
        }
    }

    private static async Task AssertValidationProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, body.RootElement.GetProperty("status").GetInt32());
        Assert.NotEmpty(body.RootElement.GetProperty("errors").EnumerateObject());
    }
}

/// <summary>An API host with no reachable database: any persistence call makes boundary validation tests fail.</summary>
public sealed class BoundaryApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("ConnectionStrings:notepal", "Host=127.0.0.1;Port=1;Database=notepal;Username=notepal;Password=boundary-tests;Timeout=1");
        builder.UseSetting("AzureAd:TenantId", "00000000-0000-0000-0000-000000000001");
        builder.UseSetting("AzureAd:ClientId", "00000000-0000-0000-0000-000000000002");
        builder.UseSetting("Ocr:Endpoint", "");
        builder.ConfigureTestServices(services =>
        {
            var worker = services.Single(descriptor =>
                descriptor.ServiceType == typeof(IHostedService) && descriptor.ImplementationType == typeof(PageProcessingService));
            services.Remove(worker);
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = TestAuthHandler.SchemeName;
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            });
        });
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, "boundary-user");
        client.DefaultRequestHeaders.Add(TestAuthHandler.EmailHeader, "caller@example.com");
        client.DefaultRequestHeaders.Add(TestAuthHandler.ScopeHeader, "access_as_user");
        return client;
    }
}
