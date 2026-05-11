using TradeFlow.Worker;

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

// Alert repository is registered as Scoped since it depends on the DbContext which is also Scoped
builder.Services.AddScoped<IAlertRepository, AlertRepository>();

// Register the alert polling service as a hosted service that runs in the background
builder.Services.AddHostedService<AlertPollingService>();

var host = builder.Build();
host.Run();
