using TradeFlow.Worker;
using TradeFlow.AlertPoC.RiskEngine;
using TradeFlow.AlertPoC.Services;
using TradeFlow.Worker.Configuration;
using System.ComponentModel.DataAnnotations;

var builder = Host.CreateApplicationBuilder(args);

// -- Configuration --

// Read configuration from environment variables, with a fallback to throw an exception if not set
var token = Environment.GetEnvironmentVariable("XTRADES_TOKEN")
    ?? throw new InvalidOperationException("XTRADES_TOKEN environment variable is not set.");

// -- Bind and validate options at startup --
builder.Services
    .AddOptions<XtradesOptions>()
    .Bind(builder.Configuration.GetSection(XtradesOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<RiskEngineOptions>()
    .Bind(builder.Configuration.GetSection(RiskEngineOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<PollingOptions>()
    .Bind(builder.Configuration.GetSection(PollingOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// -- Register services --

// HTTP client for making API calls, registered as a Singleton as its designed to be shared
builder.Services.AddSingleton<IAlertApiClient>(new AlertApiClient(token));

// Normalizer is registered as a Singleton since it is stateless and can be shared across the application
builder.Services.AddSingleton<IAlertNormalizer, AlertNormalizer>();

// Risk rules - read from options
builder.Services.AddSingleton<RiskEngineService>(sp =>
{
    var riskOptions = sp.GetRequiredService<IOptions<RiskEngineOptions>>().Value;

    var rules = new List<IRiskRule>
    {
        new EntryOnlyRule(),
        new MinXScoreRule(riskOptions.MinXScore),
        new ApprovedTraderRule(riskOptions.ApprovedTraders)
    };

    if (!riskOptions.AllowLotto)
    {
        rules.Insert(1, new NoLottoRule());
    }

    return new RiskEngineService(rules);
});

// Register the alert polling service as a hosted service that runs in the background
builder.Services.AddHostedService<AlertPollingService>();

var host = builder.Build();
host.Run();
