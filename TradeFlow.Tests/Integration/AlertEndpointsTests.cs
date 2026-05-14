using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TradeFlow.Worker.Data;

namespace TradeFlow.Tests.Integration;

public class TestApiFactory : WebApplicationFactory<Api.Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var toRemove = services
                .Where(d =>
                    d.ServiceType.FullName?.Contains("TradeFlow") == true ||
                    d.ServiceType.FullName?.Contains("DbContext") == true ||
                    d.ImplementationType?.FullName?.Contains("TradeFlow") == true ||
                    d.ImplementationType?.FullName?.Contains("DbContext") == true)
                .ToList();

            foreach (var d in toRemove)
                services.Remove(d);

            services.AddDbContext<TradeFlowDbContext>(options =>
                options.UseInMemoryDatabase("integration-test-alerts"));
        });
    }

    public void ResetDatabase()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeFlowDbContext>();
        db.Alerts.RemoveRange(db.Alerts);
        db.SaveChanges();
    }
}

/// <summary>
/// Integration tests for the Alert API endpoints.
/// Uses WebApplicationFactory to spin up the full API pipeline
/// in memory with an in-memory database replacing PostgreSQL.
/// </summary>
[Collection("Integration")]
public class AlertEndpointsTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;
    private readonly HttpClient _client;

    public AlertEndpointsTests(TestApiFactory factory)
    {
        _factory = factory;
        _factory.ResetDatabase();
        _client = _factory.CreateClient();
    }

    // -- GET /api/alerts --
    [Fact]
    public async Task GetAlerts_EmptyDatabase_Returns200WithEmptyData()
    {
        // Act
        var response = await _client.GetAsync("/api/alerts");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AlertListResponse>();
        Assert.NotNull(body);
        Assert.Equal(0, body.TotalAlerts);
        Assert.Empty(body.Data);
    }

    [Fact]
    public async Task GetAlerts_WithData_ReturnsPaginatedResults()
    {
        // Arrange, seed the in-memory database
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeFlowDbContext>();

        db.Alerts.AddRange(
            BuildEntity("id-1", "yoyomun", "TSLA", "bto", true),
            BuildEntity("id-2", "yoyomun", "AAPL", "stc", false),
            BuildEntity("id-3", "Fibonaccizer", "SPX", "bto", true)
        );
        await db.SaveChangesAsync();

        // Act
        var response = await _client.GetAsync("/api/alerts?pageSize=10");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AlertListResponse>();
        Assert.NotNull(body);
        Assert.Equal(3, body.TotalAlerts);
        Assert.Equal(3, body.Data.Count);
    }

    [Fact]
    public async Task GetAlerts_FilterByUserName_ReturnsFilteredResults()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeFlowDbContext>();

        db.Alerts.AddRange(
            BuildEntity("id-4", "yoyomun",     "TSLA", "bto", true),
            BuildEntity("id-5", "Fibonaccizer", "SPX",  "bto", true)
        );
        await db.SaveChangesAsync();

        // Act
        var response = await _client.GetAsync("/api/alerts?userName=yoyomun");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<AlertListResponse>();
        Assert.NotNull(body);
        Assert.All(body.Data, a => Assert.Equal("yoyomun", a.UserName));
    }

    // -- Validation --
    [Theory]
    [InlineData("/api/alerts?page=0",      "page")]
    [InlineData("/api/alerts?pageSize=0",  "pageSize")]
    [InlineData("/api/alerts?pageSize=101","pageSize")]
    public async Task GetAlerts_InvalidPagination_Returns400(
        string url, string expectedErrorField)
    {
        // Act
        var response = await _client.GetAsync(url);

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content
            .ReadFromJsonAsync<ValidationProblemResponse>();
        Assert.NotNull(body);
        Assert.True(body.Errors.ContainsKey(expectedErrorField));
    }

    // -- GET /api/alerts/{id} --
    [Fact]
    public async Task GetAlertById_ExistingId_Returns200()
    {
        // Arrange
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TradeFlowDbContext>();

        db.Alerts.Add(BuildEntity("existing-id", "yoyomun", "TSLA", "bto", true));
        await db.SaveChangesAsync();

        // Act
        var response = await _client.GetAsync("/api/alerts/existing-id");

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetAlertById_NonExistentId_Returns404()
    {
        // Act
        var response = await _client.GetAsync("/api/alerts/does-not-exist");

        // Assert
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // -- Helper Methods --
    private static AlertEntity BuildEntity(
        string id, string userName, string symbol,
        string side, bool riskApproved) =>
        new()
        {
            Id          = id,
            UserName    = userName,
            Symbol      = symbol,
            Side        = side,
            RiskApproved = riskApproved,
            RiskReason  = riskApproved ? "All rules passed" : "Rejected",
            IngestedAt  = DateTimeOffset.UtcNow,
            TimeOfEntryAlert = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

    // -- Response DTOs for deserialization --
    private record AlertListResponse(
        int TotalAlerts,
        int Page,
        int PageSize,
        List<AlertItem> Data);

    private record AlertItem(
        string? Id,
        string? UserName,
        string? Symbol,
        string? Side,
        bool RiskApproved);

    private record ValidationProblemResponse(
        string Title,
        int Status,
        Dictionary<string, string[]> Errors);
}