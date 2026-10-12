using Notepal.Api.Extensions;

var builder = WebApplication.CreateBuilder(args);

builder.AddNotepalApi();

var app = builder.Build();

await app.InitializeDatabaseAsync(app.Lifetime.ApplicationStopping);

app.UseNotepalApi();

app.Run();

public partial class Program;

