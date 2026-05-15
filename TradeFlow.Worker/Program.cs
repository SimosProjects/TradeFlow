using TradeFlow.Worker;
using TradeFlow.Worker.Metrics;

AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog((services, config) =>
    config.ReadFrom.Configuration(builder.Configuration)
          .Enrich.FromLogContext());

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

// -- Database --

// Get the connection string from configuration, with a fallback to throw an exception if not set
var connectionString = builder.Configuration.GetConnectionString("TradeFlow")
    ?? throw new InvalidOperationException(
        "TradeFlow connection string is not configured.");

// Register the DbContext with a scoped lifetime, which is appropriate for database contexts
builder.Services.AddDbContext<TradeFlowDbContext>(options =>
{
    options.UseNpgsql(connectionString);
    options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking); // Use NoTracking for read-only operations to improve performance
},
ServiceLifetime.Scoped);

// -- Register services --

// Register as a typed client
builder.Services.AddHttpClient<IAlertApiClient, TradeFlow.Worker.Services.AlertApiClient>(client =>
{
    client.BaseAddress = new Uri("https://app.xtrades.net");
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", token);
    client.DefaultRequestHeaders.Add("Accept", "application/json");
    client.Timeout = TimeSpan.FromSeconds(100);
})
.AddStandardResilienceHandler(options =>
{
    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
    options.Retry.MaxRetryAttempts = 3;
});

// Normalizer is registered as a Singleton since it is stateless and can be shared across the application
builder.Services.AddSingleton<IAlertNormalizer, AlertNormalizer>();

// Metrics — Singleton, Meter is thread-safe
builder.Services.AddSingleton<AlertMetrics>();

// Risk rules - read from options
builder.Services.AddSingleton<RiskEngineService>(sp =>
{
    var riskOptions = sp.GetRequiredService<IOptions<RiskEngineOptions>>().Value;

    var rules = new List<IRiskRule>
    {
        new EntryOnlyRule(),
        new MinXScoreRule(riskOptions.MinXScore),
        //new ApprovedTraderRule(riskOptions.ApprovedTraders)
    };

    if (!riskOptions.AllowLotto)
    {
        rules.Insert(1, new NoLottoRule());
    }

    return new RiskEngineService(rules);
});

// Discord notification service - Singleton, stateless
builder.Services.AddSingleton<DiscordNotificationService>();

// Alert repository is registered as Scoped since it depends on the DbContext which is also Scoped
builder.Services.AddScoped<IAlertRepository, AlertRepository>();

// Register the broker service
builder.Services.AddSingleton<IBrokerService, NullBrokerService>();

// Register the alert polling service as a hosted service that runs in the background
builder.Services.AddHostedService<AlertPollingService>();

// SignalR listener, live entry alerts from Xtrades feed
// Runs alongside the REST polling service
builder.Services.AddHostedService<SignalRListenerService>();

var host = builder.Build();
host.Run();
