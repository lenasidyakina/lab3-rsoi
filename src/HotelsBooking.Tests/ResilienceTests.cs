using System.Net;
using GatewayService.Models;
using GatewayService.Resilience;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HotelsBooking.Tests;

public class ServiceFailureTests
{
    public static TheoryData<Exception> Outages => new()
    {
        new HttpRequestException("Connection refused (payment:8060)"),
        new HttpRequestException("Bad Gateway", null, HttpStatusCode.BadGateway),
        new HttpRequestException("Internal Server Error", null, HttpStatusCode.InternalServerError),
        new TaskCanceledException("HttpClient.Timeout of 2 seconds elapsing"),
        new TimeoutException(),
        new ServiceUnavailableException("Payment")
    };

    public static TheoryData<Exception> ServiceAnswered => new()
    {
        new HttpRequestException("Not Found", null, HttpStatusCode.NotFound),
        new HttpRequestException("Bad Request", null, HttpStatusCode.BadRequest),
        new InvalidOperationException("Unexpected JSON"),
    };

    [Theory]
    [MemberData(nameof(Outages))]
    public void NetworkErrorsTimeoutsAnd5xx_AreOutages(Exception e) => Assert.True(ServiceFailure.Is(e));

    [Theory]
    [MemberData(nameof(ServiceAnswered))]
    public void ClientErrorsAndBugs_AreNotOutages(Exception e) => Assert.False(ServiceFailure.Is(e));

    [Fact]
    public async Task Wrap_TurnsOutageIntoServiceUnavailable_KeepingCause()
    {
        var cause = new HttpRequestException("Connection refused (payment:8060)");

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(
            () => ServiceFailure.Wrap("Payment", () => Task.FromException(cause)));

        Assert.Equal("Payment Service unavailable", error.Message);
        Assert.Same(cause, error.InnerException);
    }

    [Fact]
    public async Task Wrap_LetsClientErrorsThrough()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => ServiceFailure.Wrap("Reservation",
            () => Task.FromException(new HttpRequestException("Not Found", null, HttpStatusCode.NotFound))));
    }
}

public class ServiceUnavailableFilterTests
{
    private static ExceptionContext ContextWith(Exception e) =>
        new(new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor()), [])
        {
            Exception = e
        };

    [Fact]
    public void ServiceUnavailable_Becomes503WithMessage()
    {
        var context = ContextWith(new ServiceUnavailableException("Loyalty"));

        new ServiceUnavailableFilter().OnException(context);

        var result = Assert.IsType<ObjectResult>(context.Result);
        Assert.Equal(503, result.StatusCode);
        Assert.Equal(new ErrorResponse("Loyalty Service unavailable"), result.Value);
        Assert.True(context.ExceptionHandled);
    }

    [Fact]
    public void OtherExceptions_AreLeftAlone()
    {
        var context = ContextWith(new InvalidOperationException("Sequence contains no elements"));

        new ServiceUnavailableFilter().OnException(context);

        Assert.Null(context.Result);
        Assert.False(context.ExceptionHandled);
    }
}

public class RetryQueueTests
{
    private readonly List<string> _sent = [];
    private readonly RetryQueue _queue = new(
        new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
        NullLogger<RetryQueue>.Instance);

    private void EnqueueDecrement(string username, Func<bool> loyaltyIsUp) =>
        _queue.Enqueue($"loyalty decrement for {username}", _ =>
        {
            if (!loyaltyIsUp())
                throw new ServiceUnavailableException("Loyalty");
            _sent.Add(username);
            return Task.CompletedTask;
        });

    [Fact]
    public async Task LoyaltyBack_QueuedRequestsSentInOrder()
    {
        EnqueueDecrement("Ksenia Volkova", () => true);
        EnqueueDecrement("Timur Galiev", () => true);

        await _queue.ProcessAsync();

        Assert.Equal(["Ksenia Volkova", "Timur Galiev"], _sent);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task LoyaltyStillDown_StopsAtFirstFailureAndKeepsRest()
    {
        var loyaltyUp = false;
        EnqueueDecrement("Ksenia Volkova", () => loyaltyUp);
        EnqueueDecrement("Timur Galiev", () => true);

        await _queue.ProcessAsync();

        Assert.Empty(_sent);
        Assert.Equal(2, _queue.Count);

        loyaltyUp = true;
        await _queue.ProcessAsync();

        Assert.Equal(["Ksenia Volkova", "Timur Galiev"], _sent);
    }

    [Fact]
    public async Task EmptyQueue_DoesNothing()
    {
        await _queue.ProcessAsync();

        Assert.Equal(0, _queue.Count);
    }
}

public class ServiceBreakersTests
{
    [Fact]
    public async Task LoyaltyOutage_DoesNotOpenPaymentCircuit()
    {
        var breakers = new ServiceBreakers(Options.Create(new CircuitBreakerOptions { FailureThreshold = 2 }),
            TimeProvider.System, NullLoggerFactory.Instance);

        for (var i = 0; i < 2; i++)
            await breakers.Loyalty.ExecuteAsync<LoyaltyInfoResponse?>(
                () => throw new HttpRequestException("Name or service not known (loyalty:8050)"), () => null);

        Assert.Equal(CircuitState.Open, breakers.Loyalty.State);
        Assert.Equal(CircuitState.Closed, breakers.Payment.State);
        Assert.Equal(CircuitState.Closed, breakers.Reservation.State);
    }
}
