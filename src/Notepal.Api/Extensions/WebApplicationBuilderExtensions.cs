using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Identity.Web;
using Notepal.Api.Auth;
using Notepal.Api.Ocr;
using Notepal.Api.Processing;
using Notepal.Contracts;
using Notepal.Database;

namespace Notepal.Api.Extensions;

public static class WebApplicationBuilderExtensions
{
    public const string NotesUserPolicy = "NotesUser";

    public static WebApplicationBuilder AddNotepalApi(this WebApplicationBuilder builder)
    {
        builder.AddServiceDefaults();

        builder.Services.AddOpenApi();
        builder.Services.AddProblemDetails();
        builder.Services.AddValidation();

        builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = UploadLimits.MaxRequestBytes);
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = UploadLimits.MaxRequestBytes);
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            // Azure Container Apps terminates TLS at its ingress and forwards the original scheme/client IP.
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        });

        // Authentication: Entra ID access tokens issued for this API.
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

        var requiredScope = builder.Configuration["AzureAd:Scopes"] ?? "access_as_user";
        builder.Services.AddAuthorizationBuilder()
            .AddPolicy(NotesUserPolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireScope(requiredScope.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                // Every row is keyed on the user's object id, so a token without one can never be served.
                .RequireAssertion(ctx => HttpContextCurrentUser.GetUserId(ctx.User) is not null));

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, HttpContextCurrentUser>();

        builder.AddNotepalDatabase();

        builder.Services.Configure<OcrOptions>(builder.Configuration.GetSection(OcrOptions.SectionName));
        if (builder.Configuration.GetSection(OcrOptions.SectionName).Get<OcrOptions>()?.IsConfigured == true)
        {
            builder.Services.AddSingleton<IOcrClient, AzureOpenAiOcrClient>();
        }
        else
        {
            builder.Services.AddSingleton<IOcrClient, UnconfiguredOcrClient>();
        }

        builder.Services.AddSingleton<TextExtractionService>();
        builder.Services.AddSingleton<ProcessingQueue>();
        builder.Services.AddHostedService<PageProcessingService>();

        return builder;
    }
}
