
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(new ConfigurationBuilder()
        .AddJsonFile("appsettings.json")
        .AddEnvironmentVariables()
        .Build())
    .Enrich.FromLogContext()
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

// Add services to the container.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddProblemDetails();

// Add output caching
builder.Services.AddOutputCache(options =>
{
    options.AddPolicy("alerts", builder =>
        builder
            .Expire(TimeSpan.FromSeconds(30))
            .SetVaryByQuery("page", "pageSize", "userName", "symbol", "side", "riskApproved"));
});

// Configure database connection
var connectionString = builder.Configuration.GetConnectionString("TradeFlow")
    ?? throw new InvalidOperationException(
        "TradeFlow connection string is not configured.");

// Health checks
builder.Services.AddHealthChecks()
    .AddNpgSql(connectionString, name: "postgresql");

builder.Services.AddDbContext<TradeFlowDbContext>(options =>
{
    options.UseNpgsql(connectionString);
    options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
});

builder.Services.AddScoped<IAlertRepository, AlertRepository>();

// Pipeline
var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseExceptionHandler("/error");
}

app.UseHttpsRedirection();

app.UseOutputCache();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.MapAlertEndpoints();

app.Run();
