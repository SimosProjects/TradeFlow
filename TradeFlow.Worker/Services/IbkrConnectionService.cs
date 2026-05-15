using IBApi;

namespace TradeFlow.Worker.Services;

// Manages the socket connection to IB Gateway.
// The IBApi EClient is not thread-safe so all calls must go through this service.
// Singleton so only one connection shared across the application.
public class IbkrConnectionService : IDisposable
{
    private readonly IbkrOptions _options;
    private readonly ILogger<IbkrConnectionService> _logger;
    private readonly EClientSocket _client;
    private readonly IbkrEWrapper _wrapper;
    private readonly EReaderSignal  _signal;

    private bool _connected = false;

    public bool IsConnected => _connected && _client.IsConnected();

    public IbkrConnectionService(
        IOptions<IbkrOptions> options,
        ILogger<IbkrConnectionService> logger,
        ILogger<IbkrEWrapper> wrapperLogger)
    {
        _options = options.Value;
        _logger  = logger;
        _wrapper = new IbkrEWrapper(wrapperLogger);
        _signal  = new EReaderMonitorSignal();
        _client  = new EClientSocket(_wrapper, _signal);
    }

    // Connect to IB Gateway, must be called before any API calls
    public bool Connect()
    {
        if (IsConnected)
            return true;

        try
        {
            _client.eConnect(_options.Host, _options.Port, _options.ClientId);

            // Start the reader thread, processes incoming messages from IB Gateway
            var reader = new EReader(_client, _signal);
            reader.Start();

            // Background thread that processes messages as they arrive
            var readerThread = new Thread(() =>
            {
                while (_client.IsConnected())
                {
                    _signal.waitForSignal();
                    reader.processMsgs();
                }
            })
            {
                IsBackground = true,
                Name = "IbkrReaderThread"
            };
            readerThread.Start();

            // Give the connection a moment to establish
            Thread.Sleep(1000);

            _connected = _client.IsConnected();

            if (_connected)
                _logger.LogInformation(
                    "Connected to IB Gateway at {Host}:{Port} | ClientId: {ClientId}",
                    _options.Host, _options.Port, _options.ClientId);
            else
                _logger.LogError(
                    "Failed to connect to IB Gateway at {Host}:{Port}",
                    _options.Host, _options.Port);

            return _connected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception connecting to IB Gateway");
            return false;
        }
    }

    public void Disconnect()
    {
        if (_client.IsConnected())
        {
            _client.eDisconnect();
            _connected = false;
            _logger.LogInformation("Disconnected from IB Gateway.");
        }
    }

    // Expose the client for API calls in IbkrBrokerService
    public EClientSocket Client => _client;

    // Expose the wrapper for subscribing to callbacks
    public IbkrEWrapper Wrapper => _wrapper;

    public void Dispose()
    {
        Disconnect();
        _client.Close();
    }
}