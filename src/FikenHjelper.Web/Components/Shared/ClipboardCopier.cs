using Microsoft.JSInterop;
using MudBlazor;

namespace FikenHjelper.Web.Components.Shared;

/// <summary>
/// Kopierer til utklippstavlen via <c>wwwroot/js/clipboard.js</c> og viser norske meldinger, på samme måte som kvalitetssjekken.
/// Opprettes av siden og må avhendes sammen med den.
/// </summary>
public sealed class ClipboardCopier(IJSRuntime js, ISnackbar snackbar, ILogger logger) : IAsyncDisposable
{
    private IJSObjectReference? _module;

    public bool IsCopying { get; private set; }

    /// <summary>Laster modulen på forhånd, så kopieringen skjer raskt nok etter klikket (Safari og Firefox krever det).</summary>
    public async Task PreloadAsync()
    {
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/clipboard.js");
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Klarte ikke å laste clipboard.js på forhånd");
        }
    }

    /// <summary>Kopierer HTML og tekst, så innliming i Word og Google Docs gir en tabell og i et regneark gir celler.</summary>
    public Task CopyTableAsync(string html, string text) =>
        CopyAsync("copyRichText", "Tabellen er kopiert.", html, text);

    public Task CopyMarkdownAsync(string markdown) =>
        CopyAsync("copyText", "Markdown er kopiert.", markdown);

    private async Task CopyAsync(string function, string successMessage, params object[] args)
    {
        if (IsCopying)
        {
            return;
        }

        IsCopying = true;
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/clipboard.js");
            var result = await _module.InvokeAsync<string>(function, args);
            switch (result)
            {
                case "insecure":
                    snackbar.Add(
                        "Nettleseren gir ikke tilgang til utklippstavlen. Kopiering krever at siden er åpnet via https eller localhost.",
                        Severity.Warning);
                    break;
                case "text" when function == "copyRichText":
                    snackbar.Add("Kopiert som ren tekst. Nettleseren tillot ikke tabellformat.", Severity.Info);
                    break;
                default:
                    snackbar.Add(successMessage, Severity.Success);
                    break;
            }
        }
        catch (JSException ex)
        {
            logger.LogWarning(ex, "Kopiering til utklippstavlen feilet");
            snackbar.Add(
                "Nettleseren tillot ikke kopiering. Prøv igjen, eller bruk en annen nettleser (Safari kan avvise kopiering fra denne siden).",
                Severity.Warning);
        }
        catch (Exception ex) when (ex is JSDisconnectedException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Kopiering avbrutt fordi forbindelsen til nettleseren ble brutt");
        }
        finally
        {
            IsCopying = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            try
            {
                await _module.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }
}
