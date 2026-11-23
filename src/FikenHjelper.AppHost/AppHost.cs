var builder = DistributedApplication.CreateBuilder(args);

// Web-appen lytter direkte på de faste portene fra sin egen launchSettings.json (5066/7193),
// uten Aspire-proxy, slik at OAuth-redirect-URI-ene registrert i Fiken-appen fortsatt stemmer.
var web = builder.AddDotnetProject(
        "web",
        "../FikenHjelper.Web/FikenHjelper.Web.csproj",
        options => options.LaunchProfileName = "https")
    .WithEndpoint("https", endpoint => endpoint.IsProxied = false)
    .WithEndpoint("http", endpoint => endpoint.IsProxied = false);

// Valgfrie hemmeligheter fra AppHost sine user-secrets (Parameters:<navn>).
// Kobles bare til når de finnes, så oppstart aldri stopper på manglende verdi.
// Web-appens egne user-secrets og %LocalAppData%\FikenHjelper\config.json virker uansett.
AddOptionalSecret("fiken-client-id", "Fiken__ClientId");
AddOptionalSecret("fiken-client-secret", "Fiken__ClientSecret");
AddOptionalSecret("fiken-api-token", "Fiken__ApiToken");

builder.Build().Run();

void AddOptionalSecret(string parameterName, string environmentVariable)
{
    if (string.IsNullOrWhiteSpace(builder.Configuration[$"Parameters:{parameterName}"]))
    {
        return;
    }

    var parameter = builder.AddParameter(parameterName, secret: true);
    web.WithEnvironment(environmentVariable, parameter);
}
