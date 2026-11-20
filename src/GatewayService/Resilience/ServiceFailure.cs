namespace GatewayService.Resilience;

public sealed class ServiceUnavailableException(string service, Exception? inner = null)
    : Exception($"{service} Service unavailable", inner)
{
    public string Service { get; } = service;
}

public static class ServiceFailure
{
    /// <summary>Сервис не ответил: сетевая ошибка, таймаут или 5xx.</summary>
    public static bool Is(Exception e) => e switch
    {
        ServiceUnavailableException => true,
        HttpRequestException http => http.StatusCode is null || (int)http.StatusCode >= 500,
        TaskCanceledException or TimeoutException => true,
        _ => false
    };

    /// <summary>Вызов без Circuit Breaker (для изменяющих операций): ошибка сервиса → ServiceUnavailableException.</summary>
    public static async Task<T> Wrap<T>(string service, Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception e) when (Is(e) && e is not ServiceUnavailableException)
        {
            throw new ServiceUnavailableException(service, e);
        }
    }

    public static Task Wrap(string service, Func<Task> action) =>
        Wrap(service, async () =>
        {
            await action();
            return true;
        });
}
