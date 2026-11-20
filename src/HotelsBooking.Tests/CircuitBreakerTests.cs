using System.Net;
using GatewayService.Models;
using GatewayService.Resilience;
using Microsoft.Extensions.Logging.Abstractions;

namespace HotelsBooking.Tests;

public class CircuitBreakerTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = new(2026, 11, 20, 3, 14, 27, TimeSpan.FromHours(3));
        public override DateTimeOffset GetUtcNow() => Now;
        public void Wait(double seconds) => Now = Now.AddSeconds(seconds);
    }

    private sealed class FakeLoyaltyService
    {
        public bool IsStopped { get; set; }
        public int ReceivedRequests { get; private set; }

        public Task<LoyaltyInfoResponse> GetAsync(string username)
        {
            ReceivedRequests++;
            if (IsStopped)
                throw new HttpRequestException("Name or service not known (loyalty:8050)");

            return Task.FromResult(username switch
            {
                "Ksenia Volkova" => new LoyaltyInfoResponse("SILVER", 7, 13),
                "Timur Galiev" => new LoyaltyInfoResponse("BRONZE", 5, 2),
                _ => new LoyaltyInfoResponse("GOLD", 10, 27)
            });
        }
    }

    private static readonly LoyaltyInfoResponse Unknown = new("", 0, 0);

    private readonly ManualClock _clock = new();
    private readonly FakeLoyaltyService _loyalty = new();

    private CircuitBreaker CreateBreaker(int failures = 3, int windowSeconds = 30, int openSeconds = 3) =>
        new(ServiceBreakers.LoyaltyName,
            new CircuitBreakerOptions
            {
                FailureThreshold = failures,
                FailureWindow = TimeSpan.FromSeconds(windowSeconds),
                OpenTimeout = TimeSpan.FromSeconds(openSeconds)
            },
            _clock, NullLogger.Instance);

    private Task<LoyaltyInfoResponse> AskLoyalty(CircuitBreaker breaker, string username) =>
        breaker.ExecuteAsync(() => _loyalty.GetAsync(username), () => Unknown);

    [Fact]
    public async Task LoyaltyStopped_FourthRequestGetsFallbackWithoutCallingService()
    {
        var breaker = CreateBreaker(failures: 3);
        _loyalty.IsStopped = true;

        Assert.Same(Unknown, await AskLoyalty(breaker, "Ksenia Volkova"));
        _clock.Wait(2.1);
        Assert.Same(Unknown, await AskLoyalty(breaker, "Timur Galiev"));
        _clock.Wait(1.7);
        Assert.Same(Unknown, await AskLoyalty(breaker, "Ksenia Volkova"));

        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.Equal(3, _loyalty.ReceivedRequests);

        Assert.Same(Unknown, await AskLoyalty(breaker, "Timur Galiev"));
        Assert.Equal(3, _loyalty.ReceivedRequests);
    }

    [Fact]
    public async Task RareFailuresSpreadOverTime_DoNotOpenCircuit()
    {
        var breaker = CreateBreaker(failures: 3, windowSeconds: 30);
        _loyalty.IsStopped = true;

        await AskLoyalty(breaker, "Ksenia Volkova");
        _clock.Wait(12);
        await AskLoyalty(breaker, "Timur Galiev");
        _clock.Wait(25);
        await AskLoyalty(breaker, "Ksenia Volkova");

        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(2, breaker.FailureCount);
    }

    [Fact]
    public async Task FailuresAtWindowEdge_StillCountTogether()
    {
        var breaker = CreateBreaker(failures: 3, windowSeconds: 30);
        _loyalty.IsStopped = true;

        await AskLoyalty(breaker, "Ksenia Volkova");
        _clock.Wait(15);
        await AskLoyalty(breaker, "Timur Galiev");
        _clock.Wait(15);
        await AskLoyalty(breaker, "Ksenia Volkova");

        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public async Task OpenTimeoutNotElapsed_ServiceIsNotDisturbed()
    {
        var breaker = CreateBreaker(failures: 2, openSeconds: 3);
        _loyalty.IsStopped = true;
        await AskLoyalty(breaker, "Timur Galiev");
        await AskLoyalty(breaker, "Timur Galiev");

        _loyalty.IsStopped = false;
        _clock.Wait(2.9);

        Assert.Same(Unknown, await AskLoyalty(breaker, "Ksenia Volkova"));
        Assert.Equal(2, _loyalty.ReceivedRequests);
        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public async Task LoyaltyBackOnline_ProbeRequestClosesCircuit()
    {
        var breaker = CreateBreaker(failures: 2, openSeconds: 3);
        _loyalty.IsStopped = true;
        await AskLoyalty(breaker, "Timur Galiev");
        await AskLoyalty(breaker, "Timur Galiev");
        Assert.Equal(CircuitState.Open, breaker.State);

        _loyalty.IsStopped = false;
        _clock.Wait(3.4);

        var loyalty = await AskLoyalty(breaker, "Ksenia Volkova");

        Assert.Equal(new LoyaltyInfoResponse("SILVER", 7, 13), loyalty);
        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.Equal(0, breaker.FailureCount);
    }

    [Fact]
    public async Task LoyaltyStillDown_FailedProbeOpensCircuitAgain()
    {
        var breaker = CreateBreaker(failures: 2, openSeconds: 3);
        _loyalty.IsStopped = true;
        await AskLoyalty(breaker, "Timur Galiev");
        await AskLoyalty(breaker, "Timur Galiev");
        _clock.Wait(3.2);

        await AskLoyalty(breaker, "Ksenia Volkova");

        Assert.Equal(CircuitState.Open, breaker.State);
        var requestsSoFar = _loyalty.ReceivedRequests;
        await AskLoyalty(breaker, "Ksenia Volkova");
        Assert.Equal(requestsSoFar, _loyalty.ReceivedRequests);
    }

    [Fact]
    public async Task HalfOpen_LetsOnlyOneProbeThrough_OthersGetFallback()
    {
        var breaker = CreateBreaker(failures: 1, openSeconds: 3);
        _loyalty.IsStopped = true;
        await AskLoyalty(breaker, "Timur Galiev");
        _clock.Wait(3.5);

        var slowAnswer = new TaskCompletionSource<LoyaltyInfoResponse>();
        var probe = breaker.ExecuteAsync(() => slowAnswer.Task, () => Unknown);

        Assert.Equal(CircuitState.HalfOpen, breaker.State);
        Assert.Same(Unknown, await AskLoyalty(breaker, "Ksenia Volkova"));

        slowAnswer.SetResult(new LoyaltyInfoResponse("GOLD", 10, 27));
        Assert.Equal("GOLD", (await probe).Status);
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public async Task BadRequestFromService_IsNotTreatedAsOutage()
    {
        var breaker = CreateBreaker(failures: 1);

        await Assert.ThrowsAsync<HttpRequestException>(() => breaker.ExecuteAsync<LoyaltyInfoResponse>(
            () => throw new HttpRequestException("X-User-Name header is required", null, HttpStatusCode.BadRequest),
            () => Unknown));

        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public async Task InternalServerErrorAndTimeout_AreTreatedAsOutage()
    {
        var breaker = CreateBreaker(failures: 2);

        await breaker.ExecuteAsync<LoyaltyInfoResponse>(
            () => throw new HttpRequestException("relation \"loyalty\" does not exist", null,
                HttpStatusCode.InternalServerError),
            () => Unknown);
        await breaker.ExecuteAsync<LoyaltyInfoResponse>(
            () => throw new TaskCanceledException(
                "The request was canceled due to the configured HttpClient.Timeout of 2 seconds elapsing."),
            () => Unknown);

        Assert.Equal(CircuitState.Open, breaker.State);
    }

    [Fact]
    public async Task WithoutFallback_GatewayGetsServiceUnavailableMessage()
    {
        var breaker = CreateBreaker();
        _loyalty.IsStopped = true;

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => breaker.ExecuteAsync(() => _loyalty.GetAsync("Ksenia Volkova")));

        Assert.Equal("Loyalty Service unavailable", error.Message);
    }
}
