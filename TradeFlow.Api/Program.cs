using Microsoft.EntityFrameworkCore;
using TradeFlow.Worker.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Configure database connection
var connectionString = builder.Configuration.GetConnectionString("TradeFlow")
    ?? throw new InvalidOperationException(
        "TradeFlow connection string is not configured.");

builder.Services.AddDbContext<TradeFlowDbContext>(options =>
{
    options.UseNpgsql(connectionString);
    options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
});

builder.Services.AddScoped<IAlertRepository, AlertRepository>();

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

app.Run();
