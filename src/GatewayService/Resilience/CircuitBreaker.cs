namespace GatewayService.Resilience;

public enum CircuitState
{
    Closed,
    Open,
    HalfOpen
}

public sealed class CircuitBreakerOptions
{
    /// <summary>N — сколько неудачных запросов подряд (в пределах окна) размыкают цепь.</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>Окно, в котором учитываются ошибки. Более старые ошибки «забываются».</summary>
    public TimeSpan FailureWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Сколько цепь остаётся разомкнутой, прежде чем пропустить пробный запрос.</summary>
    public TimeSpan OpenTimeout { get; set; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Circuit Breaker для одного удалённого сервиса.
/// <para>
/// Статистика ошибок хранится в памяти в циклическом буфере ёмкостью N (FailureThreshold),
/// каждый элемент — timestamp неудачного запроса. Цепь размыкается, когда в буфере
/// набралось N ошибок, и все они случились в пределах FailureWindow
/// (т.е. учитывается и количество, и время).
/// </para>
/// <para>
/// Closed   — запросы идут в сервис, ошибки пишутся в буфер.<br/>
/// Open     — запрос в сервис не выполняется, сразу отдаётся fallback (N+1-й запрос).<br/>
/// HalfOpen — по истечении OpenTimeout пропускается один пробный запрос к реальной системе:
///            успех → Closed, ошибка → снова Open.
/// </para>
/// </summary>
public sealed class CircuitBreaker
{
    private readonly object _lock = new();
    private readonly RingBuffer<DateTimeOffset> _failures;
    private readonly CircuitBreakerOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private CircuitState _state = CircuitState.Closed;
    private DateTimeOffset _openedAt;
    private bool _probeInFlight;

    public CircuitBreaker(string name, CircuitBreakerOptions options, TimeProvider time, ILogger logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.FailureThreshold);
        Name = name;
        _options = options;
        _time = time;
        _logger = logger;
        _failures = new RingBuffer<DateTimeOffset>(options.FailureThreshold);
    }

    public string Name { get; }

    public CircuitState State
    {
        get
        {
            lock (_lock) return _state;
        }
    }

    /// <summary>Количество ошибок, накопленных в текущем окне.</summary>
    public int FailureCount
    {
        get
        {
            lock (_lock)
            {
                DropExpired(_time.GetUtcNow());
                return _failures.Count;
            }
        }
    }

    /// <summary>Выполнить запрос; при недоступности сервиса — ServiceUnavailableException.</summary>
    public Task<T> ExecuteAsync<T>(Func<Task<T>> action) =>
        ExecuteAsync(action, () => throw new ServiceUnavailableException(Name));

    /// <summary>Выполнить запрос; при недоступности сервиса или разомкнутой цепи — fallback.</summary>
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> action, Func<T> fallback)
    {
        if (!TryAcquire())
        {
            _logger.LogInformation("Circuit {Name} is open, returning fallback", Name);
            return fallback();
        }

        T result;
        try
        {
            result = await action();
        }
        catch (Exception e) when (ServiceFailure.Is(e))
        {
            _logger.LogWarning("Request to {Name} failed: {Error}", Name, e.Message);
            RecordFailure();
            return fallback();
        }
        catch
        {
            // сервис ответил (например, 4xx) — значит он жив
            RecordSuccess();
            throw;
        }

        RecordSuccess();
        return result;
    }

    private bool TryAcquire()
    {
        lock (_lock)
        {
            switch (_state)
            {
                case CircuitState.Closed:
                    return true;

                case CircuitState.Open:
                    if (_time.GetUtcNow() - _openedAt < _options.OpenTimeout)
                        return false;

                    _state = CircuitState.HalfOpen;
                    _probeInFlight = true;
                    _logger.LogInformation("Circuit {Name}: Open -> HalfOpen, sending probe request", Name);
                    return true;

                case CircuitState.HalfOpen:
                    // пока идёт пробный запрос, остальные получают fallback
                    if (_probeInFlight)
                        return false;
                    _probeInFlight = true;
                    return true;

                default:
                    return false;
            }
        }
    }

    private void RecordSuccess()
    {
        lock (_lock)
        {
            if (_state != CircuitState.HalfOpen)
                return;

            _state = CircuitState.Closed;
            _probeInFlight = false;
            _failures.Clear();
            _logger.LogInformation("Circuit {Name}: HalfOpen -> Closed", Name);
        }
    }

    private void RecordFailure()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            switch (_state)
            {
                case CircuitState.HalfOpen:
                    Trip(now);
                    return;

                case CircuitState.Open:
                    return; // запрос, начатый до размыкания — статистика уже не нужна
            }

            DropExpired(now);
            _failures.Add(now);

            if (_failures.IsFull)
                Trip(now);
        }
    }

    private void DropExpired(DateTimeOffset now)
    {
        while (!_failures.IsEmpty && now - _failures.Oldest > _options.FailureWindow)
            _failures.RemoveOldest();
    }

    private void Trip(DateTimeOffset now)
    {
        _state = CircuitState.Open;
        _openedAt = now;
        _probeInFlight = false;
        _failures.Clear();
        _logger.LogWarning("Circuit {Name} -> Open for {Timeout}", Name, _options.OpenTimeout);
    }
}
