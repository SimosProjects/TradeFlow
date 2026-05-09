using TradeFlow.Worker;
using TradeFlow.AlertPoC.RiskEngine;
using TradeFlow.AlertPoC.Services;

var builder = Host.CreateApplicationBuilder(args);

// -- Configuration --

// Read configuration from environment variables, with a fallback to throw an exception if not set
var token = Environment.GetEnvironmentVariable("XTRADES_TOKEN")
    ?? throw new InvalidOperationException("XTRADES_TOKEN environment variable is not set.");

// -- Register services --

// HTTP client for making API calls, registered as a Singleton as its designed to be shared
builder.Services.AddSingleton<IAlertApiClient>(new AlertApiClient(token));

// Normalizer is registered as a Singleton since it is stateless and can be shared across the application
builder.Services.AddSingleton<IAlertNormalizer, AlertNormalizer>();

// Risk rules are registered as Transient since they are stateless and can be created on demand
builder.Services.AddTransient<EntryOnlyRule>();
builder.Services.AddTransient<NoLottoRule>();
builder.Services.AddTransient<MinXScoreRule>(_ => new MinXScoreRule(60));
builder.Services.AddTransient<ApprovedTraderRule>(_ =>
    new ApprovedTraderRule([
        "yoyomun", "Fibonaccizer", "Atlas"
    ]));

// Register the RiskEngineService Singleton, rules are injected at construction
builder.Services.AddSingleton<RiskEngineService>(sp => new RiskEngineService([
    sp.GetRequiredService<EntryOnlyRule>(),
    sp.GetRequiredService<NoLottoRule>(),
    sp.GetRequiredService<MinXScoreRule>(),
    sp.GetRequiredService<ApprovedTraderRule>()
]));

// Register the alert polling service as a hosted service that runs in the background
builder.Services.AddHostedService<AlertPollingService>();

// -- Validate registration at startup --

// Validate the service provider configuration at startup to catch any issues early
builder.Services.BuildServiceProvider(new ServiceProviderOptions
{
    ValidateOnBuild = true,
    ValidateScopes = true
});

var host = builder.Build();
host.Run();
