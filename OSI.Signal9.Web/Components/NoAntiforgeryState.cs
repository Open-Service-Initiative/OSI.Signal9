using Microsoft.AspNetCore.Components.Forms;

namespace OSI.Signal9.Web.Components;

/// <summary>
/// Gives the dashboard no antiforgery token. Its pages take no form posts; every state-changing request carries the
/// custom CSRF header instead (story 47). Without this, Blazor would set an antiforgery cookie on every page.
/// </summary>
internal sealed class NoAntiforgeryState : AntiforgeryStateProvider
{
    public override AntiforgeryRequestToken? GetAntiforgeryToken() => null;
}
