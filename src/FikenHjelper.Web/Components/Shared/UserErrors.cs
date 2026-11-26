using System.Text.Json;
using FikenHjelper.Core.Fiken;

namespace FikenHjelper.Web.Components.Shared;

/// <summary>
/// Gjør unntak om til norske feilmeldinger kassereren kan handle på. Tekniske detaljer havner i loggen,
/// ikke i meldingen.
/// </summary>
internal static class UserErrors
{
    private const string TryAgain = "Prøv igjen om litt. Hvis det fortsetter, last inn siden på nytt eller sjekk tilkoblingen under Innstillinger.";

    /// <summary>
    /// Melding for en feil. <paramref name="failed"/> beskriver det som feilet, f.eks. «Klarte ikke å hente rapporten».
    /// Skiller mellom feil fra Fiken og feil med den lokale budsjettfilen.
    /// </summary>
    public static string Describe(Exception ex, string failed, string file = "Budsjettfilen (budgets.json i datamappen)") => ex switch
    {
        FikenApiException api => $"{failed}. {api.UserMessage}",
        HttpRequestException or TimeoutException or TaskCanceledException => $"{failed}. Fiken svarte ikke. Prøv igjen om litt.",
        JsonException => $"{failed}. {file} er skadet og kan ikke leses. Rett den, eller ta en sikkerhetskopi og slett den for å starte på nytt.",
        IOException or UnauthorizedAccessException => $"{failed}. {file} er låst eller mangler skrivetilgang. Lukk andre programmer som bruker den, sjekk at det er ledig diskplass, og prøv igjen.",
        _ => $"{failed}. {TryAgain}"
    };
}
