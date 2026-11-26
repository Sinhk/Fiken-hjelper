using System.Reflection;
using FikenHjelper.Mcp;
using FikenHjelper.Mcp.Tools;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

// MCP-server over stdio. stdout er protokollkanalen, så all logging går til stderr.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

// Samme user-secrets som Web (Fiken:ClientId o.l.), også når miljøet ikke er Development.
builder.Configuration.AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true);
builder.Configuration.AddEnvironmentVariables();

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Information);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

builder.Services.AddFikenHjelperMcp(builder.Configuration);
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new Implementation
        {
            Name = "fiken-hjelper",
            Title = "Fiken-hjelper (Romsdal og Nordmøre krets)",
            Version = typeof(McpInstructions).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"
        };
        options.ServerInstructions = McpInstructions.Text;
    })
    .WithStdioServerTransport()
    .WithTools<ContextTools>(ToolSupport.JsonOptions)
    .WithTools<InboxTools>(ToolSupport.JsonOptions)
    .WithTools<PurchaseTools>(ToolSupport.JsonOptions)
    .WithTools<BudgetTools>(ToolSupport.JsonOptions)
    .WithTools<AnomalyTools>(ToolSupport.JsonOptions)
    .WithTools<GrantTools>(ToolSupport.JsonOptions)
    .WithPrompts<FikenPrompts>(ToolSupport.JsonOptions);

await builder.Build().RunAsync();
