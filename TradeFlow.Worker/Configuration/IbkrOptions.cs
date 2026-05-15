namespace TradeFlow.Worker.Configuration;

public class IbkrOptions
{
    // IB Gateway host — localhost for local dev, gateway container name for Docker
    public string Host { get; set; } = "127.0.0.1";

    // 4002 = paper trading, 4001 = live trading
    public int Port { get; set; } = 4002;

    // Unique client ID, must be different for each connected client
    public int ClientId { get; set; } = 1;

    // Paper account number
    public string AccountId { get; set; } = string.Empty;

    // Timeout for connection and order confirmations
    public int TimeoutMs { get; set; } = 5000;
}