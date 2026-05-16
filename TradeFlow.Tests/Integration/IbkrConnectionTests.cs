using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TradeFlow.Worker.Configuration;
using TradeFlow.Worker.Services;

namespace TradeFlow.Tests.Integration;

// These tests require IB Gateway running on localhost:4002.
public class IbkrConnectionTests
{
    private static bool ShouldSkip =>
        Environment.GetEnvironmentVariable("SKIP_IBKR_TESTS") == "true";

    private static IbkrConnectionService BuildConnectionService()
    {
        var options = Options.Create(new IbkrOptions
        {
            Host = "127.0.0.1",
            Port = 4002,
            ClientId = 99, // use a unique client ID for tests
            AccountId = Environment.GetEnvironmentVariable("IBKR__ACCOUNTID") ?? "",
            TimeoutMs = 5000
        });

        return new IbkrConnectionService(
            options,
            NullLogger<IbkrConnectionService>.Instance,
            NullLogger<IbkrEWrapper>.Instance);
    }

    [Fact]
    public void Connect_WithGatewayRunning_ReturnsTrue()
    {
        if (ShouldSkip) return;

        var service = BuildConnectionService();
        var connected = service.Connect();

        connected.Should().BeTrue();
        service.IsConnected.Should().BeTrue();

        service.Dispose();
    }

    [Fact]
    public async Task GetAccountBalanceAsync_ReturnsPositiveBalance()
    {
        if (ShouldSkip) return;

        var connection = BuildConnectionService();
        var options = Options.Create(new IbkrOptions
        {
            Host = "127.0.0.1",
            Port = 4002,
            ClientId = 99,
            AccountId = Environment.GetEnvironmentVariable("IBKR__ACCOUNTID") ?? "",
            TimeoutMs = 5000
        });

        var broker = new IbkrBrokerService(
            connection,
            options,
            NullLogger<IbkrBrokerService>.Instance);

        var balance = await broker.GetAccountBalanceAsync();

        balance.Should().BeGreaterThan(0);

        connection.Dispose();
    }

    [Fact]
    public async Task GetOpenPositionsValueAsync_ReturnsNonNegativeValue()
    {
        if (ShouldSkip) return;

        var connection = BuildConnectionService();
        var options = Options.Create(new IbkrOptions
        {
            Host = "127.0.0.1",
            Port = 4002,
            ClientId = 99,
            AccountId = Environment.GetEnvironmentVariable("IBKR__ACCOUNTID") ?? "",
            TimeoutMs = 5000
        });

        var broker = new IbkrBrokerService(
            connection,
            options,
            NullLogger<IbkrBrokerService>.Instance);

        var value = await broker.GetOpenPositionsValueAsync();

        value.Should().BeGreaterThanOrEqualTo(0);

        connection.Dispose();
    }

    [Fact]
    public async Task GetAccountBalanceAsync_WithGatewayDown_ReturnsZero()
    {
        if (ShouldSkip) return;

        // Point to a port with nothing running
        var options = Options.Create(new IbkrOptions
        {
            Host = "127.0.0.1",
            Port = 9999,
            ClientId = 99,
            AccountId = "",
            TimeoutMs = 2000
        });

        var connection = new IbkrConnectionService(
            options,
            NullLogger<IbkrConnectionService>.Instance,
            NullLogger<IbkrEWrapper>.Instance);

        var broker = new IbkrBrokerService(
            connection,
            options,
            NullLogger<IbkrBrokerService>.Instance);

        var balance = await broker.GetAccountBalanceAsync();

        // Should return 0 gracefully rather than throwing
        balance.Should().Be(0);

        connection.Dispose();
    }
}