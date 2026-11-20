using Microsoft.Extensions.Options;

namespace GatewayService.Resilience;

public sealed class ServiceBreakers(
    IOptions<CircuitBreakerOptions> options, TimeProvider time, ILoggerFactory loggerFactory)
{
    public const string ReservationName = "Reservation";
    public const string PaymentName = "Payment";
    public const string LoyaltyName = "Loyalty";

    public CircuitBreaker Reservation { get; } = Create(ReservationName, options, time, loggerFactory);
    public CircuitBreaker Payment { get; } = Create(PaymentName, options, time, loggerFactory);
    public CircuitBreaker Loyalty { get; } = Create(LoyaltyName, options, time, loggerFactory);

    private static CircuitBreaker Create(string name, IOptions<CircuitBreakerOptions> options, TimeProvider time,
        ILoggerFactory loggerFactory) =>
        new(name, options.Value, time, loggerFactory.CreateLogger($"CircuitBreaker.{name}"));
}
