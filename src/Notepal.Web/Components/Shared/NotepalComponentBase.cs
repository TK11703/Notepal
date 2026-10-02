using Microsoft.AspNetCore.Components;
using Notepal.Web.Services;

namespace Notepal.Web.Components.Shared;

/// <summary>Base class for pages that call the API: shows friendly errors and re-authenticates when the session expired.</summary>
public abstract class NotepalComponentBase : ComponentBase
{
    [Inject] protected NotepalApiClient Api { get; set; } = null!;
    [Inject] protected NavigationManager Navigation { get; set; } = null!;
    [Inject] private ILogger<NotepalComponentBase> Logger { get; set; } = null!;

    protected string? ErrorMessage { get; set; }
    protected bool Busy { get; private set; }

    protected async Task<bool> RunAsync(Func<Task> action)
    {
        Busy = true;
        ErrorMessage = null;
        try
        {
            await action();
            return true;
        }
        catch (ReauthenticationRequiredException)
        {
            var returnUrl = "/" + Navigation.ToBaseRelativePath(Navigation.Uri);
            Navigation.NavigateTo($"authentication/login?returnUrl={Uri.EscapeDataString(returnUrl)}", forceLoad: true);
            return false;
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogError(ex, "Unexpected error calling the Notepal API");
            ErrorMessage = "Something went wrong talking to the Notepal service. Please try again.";
            return false;
        }
        finally
        {
            Busy = false;
        }
    }
}
