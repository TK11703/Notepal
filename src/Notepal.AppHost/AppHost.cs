var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);

var database = postgres.AddDatabase("notepal");

var api = builder.AddProject<Projects.Notepal_Api>("api", launchProfileName: "https")
    .WithReference(database)
    .WaitFor(database)
    .WithHttpHealthCheck("/healthz");

// The https profile keeps https://localhost:7137, which is the redirect URI registered in Entra ID.
builder.AddProject<Projects.Notepal_Web>("web", launchProfileName: "https")
    .WithExternalHttpEndpoints()
    // The web app calls the API through Microsoft.Identity.Web's downstream API, which reads a base URL rather than service discovery.
    .WithEnvironment("NotepalApi__BaseUrl", ReferenceExpression.Create($"{api.GetEndpoint("https")}/"))
    .WaitFor(api)
    .WithHttpHealthCheck("/healthz");

builder.Build().Run();
