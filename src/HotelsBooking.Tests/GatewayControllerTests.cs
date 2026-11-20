using System.Net;
using GatewayService.Clients;
using GatewayService.Controllers;
using GatewayService.Models;
using GatewayService.Resilience;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace HotelsBooking.Tests;

public class GatewayControllerTests
{
    private const string Ksenia = "Ksenia Volkova";

    private static readonly Guid AraratUid = Guid.Parse("049161bb-badd-4fa8-9d90-87c9a82b0668");
    private static readonly Guid ReservationUid = Guid.Parse("7d3c1a5e-2f4b-4e8a-9c61-0b8e5f2a9d47");
    private static readonly Guid PaymentUid = Guid.Parse("b1e2c3d4-5f60-4718-8a9b-0c1d2e3f4a5b");

    private static readonly HotelResponse Ararat = new(
        AraratUid, "Ararat Park Hyatt Moscow", "Россия", "Москва", "Неглинная ул., 4", 5, 10000);

    private static readonly ReservationDto KseniaReservation = new(
        ReservationUid, Ksenia, PaymentUid, "PAID", new DateOnly(2026, 12, 29), new DateOnly(2027, 1, 2), Ararat);

    private readonly FakeService _reservation = new("reservation:8070");
    private readonly FakeService _payment = new("payment:8060");
    private readonly FakeService _loyalty = new("loyalty:8050");
    private readonly RetryQueue _retryQueue;
    private readonly GatewayController _gateway;

    public GatewayControllerTests()
    {
        _reservation
            .On(HttpMethod.Get, $"/api/v1/hotels/{AraratUid}", Ararat)
            .On(HttpMethod.Get, "/api/v1/hotels", new PaginationResponse(1, 10, 1, [Ararat]))
            .On(HttpMethod.Get, "/api/v1/reservations", new List<ReservationDto> { KseniaReservation })
            .On(HttpMethod.Get, $"/api/v1/reservations/{ReservationUid}", KseniaReservation)
            .On(HttpMethod.Post, "/api/v1/reservations", KseniaReservation)
            .On(HttpMethod.Delete, $"/api/v1/reservations/{ReservationUid}", null, HttpStatusCode.NoContent);

        _payment
            .On(HttpMethod.Get, $"/api/v1/payments/{PaymentUid}", new PaymentDto(PaymentUid, "PAID", 37200))
            .On(HttpMethod.Post, "/api/v1/payments", new PaymentDto(PaymentUid, "PAID", 37200))
            .On(HttpMethod.Delete, $"/api/v1/payments/{PaymentUid}", null, HttpStatusCode.NoContent);

        _loyalty
            .On(HttpMethod.Get, "/api/v1/loyalty", new LoyaltyInfoResponse("SILVER", 7, 13))
            .On(HttpMethod.Post, "/api/v1/loyalty/increment", new LoyaltyInfoResponse("SILVER", 7, 14))
            .On(HttpMethod.Post, "/api/v1/loyalty/decrement", new LoyaltyInfoResponse("SILVER", 7, 12));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new CircuitBreakerOptions()));
        services.AddSingleton<ServiceBreakers>();
        services.AddSingleton<RetryQueue>();
        services.AddTransient(sp => new ReservationClient(_reservation.CreateClient(), sp.GetRequiredService<ServiceBreakers>()));
        services.AddTransient(sp => new PaymentClient(_payment.CreateClient(), sp.GetRequiredService<ServiceBreakers>()));
        services.AddTransient(sp => new LoyaltyClient(_loyalty.CreateClient(), sp.GetRequiredService<ServiceBreakers>()));
        services.AddTransient<GatewayController>();

        var provider = services.BuildServiceProvider();
        _retryQueue = provider.GetRequiredService<RetryQueue>();
        _gateway = provider.GetRequiredService<GatewayController>();
    }

    private static T OkValue<T>(ActionResult<T> result) =>
        Assert.IsType<T>(Assert.IsType<OkObjectResult>(result.Result).Value);

    private Task<ActionResult<CreateReservationResponse>> BookArarat() =>
        _gateway.CreateReservation(Ksenia,
            new CreateReservationRequest(AraratUid, new DateOnly(2026, 12, 29), new DateOnly(2027, 1, 2)));

    [Fact]
    public async Task Me_LoyaltyStopped_ReservationsShownWithEmptyLoyalty()
    {
        _loyalty.IsStopped = true;

        var me = OkValue(await _gateway.GetMe(Ksenia));

        Assert.Same(EmptyObject.Instance, me.Loyalty);
        var reservation = Assert.Single(me.Reservations);
        Assert.Equal("Ararat Park Hyatt Moscow", reservation.Hotel.Name);
        Assert.Equal("Россия, Москва, Неглинная ул., 4", reservation.Hotel.FullAddress);
    }

    [Fact]
    public async Task Reservation_PaymentStopped_ShownWithEmptyPayment()
    {
        _payment.IsStopped = true;

        var reservation = OkValue(await _gateway.GetReservation(ReservationUid, Ksenia));

        Assert.Equal("PAID", reservation.Status);
        Assert.Same(EmptyObject.Instance, reservation.Payment);
    }

    [Fact]
    public async Task Hotels_ReservationStopped_ServiceUnavailable()
    {
        _reservation.IsStopped = true;

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(() => _gateway.GetHotels());

        Assert.Equal("Reservation Service unavailable", error.Message);
    }

    [Fact]
    public async Task Booking_AllServicesUp_PriceIncludesSilverDiscount()
    {
        var booking = OkValue(await BookArarat());

        Assert.Equal(7, booking.Discount);
        Assert.Equal(new PaymentInfo("PAID", 37200), booking.Payment);
        Assert.Contains("POST /api/v1/loyalty/increment", _loyalty.Requests);
    }

    [Fact]
    public async Task Booking_LoyaltyStopped_NothingIsCreated()
    {
        _loyalty.IsStopped = true;

        var error = await Assert.ThrowsAsync<ServiceUnavailableException>(BookArarat);

        Assert.Equal("Loyalty Service unavailable", error.Message);
        Assert.Empty(_payment.Requests);
        Assert.DoesNotContain("POST /api/v1/reservations", _reservation.Requests);
    }

    [Fact]
    public async Task Booking_LoyaltyIncrementFails_PaymentRolledBack()
    {
        _loyalty.On(HttpMethod.Post, "/api/v1/loyalty/increment", null, HttpStatusCode.ServiceUnavailable);

        await Assert.ThrowsAsync<ServiceUnavailableException>(BookArarat);

        Assert.Equal(["POST /api/v1/payments", $"DELETE /api/v1/payments/{PaymentUid}"], _payment.Requests);
        Assert.DoesNotContain("POST /api/v1/reservations", _reservation.Requests);
    }

    [Fact]
    public async Task Booking_ReservationCreateFails_PaymentAndLoyaltyRolledBack()
    {
        _reservation.On(HttpMethod.Post, "/api/v1/reservations", null, HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<ServiceUnavailableException>(BookArarat);

        Assert.Contains($"DELETE /api/v1/payments/{PaymentUid}", _payment.Requests);
        Assert.Equal("POST /api/v1/loyalty/decrement", _loyalty.Requests[^1]);
    }

    [Fact]
    public async Task Cancel_LoyaltyStopped_UserGetsSuccessAndDecrementIsQueued()
    {
        _loyalty.IsStopped = true;

        Assert.IsType<NoContentResult>(await _gateway.CancelReservation(ReservationUid, Ksenia));
        Assert.Equal(1, _retryQueue.Count);

        _loyalty.IsStopped = false;
        _loyalty.Requests.Clear();
        var loyalty = OkValue(await _gateway.GetLoyalty(Ksenia));

        Assert.Equal(["POST /api/v1/loyalty/decrement", "GET /api/v1/loyalty"], _loyalty.Requests);
        Assert.Equal(0, _retryQueue.Count);
        Assert.Equal("SILVER", loyalty.Status);
    }

    [Fact]
    public async Task Cancel_PaymentStopped_UserGetsSuccessAndPaymentCancelIsQueued()
    {
        _payment.IsStopped = true;

        Assert.IsType<NoContentResult>(await _gateway.CancelReservation(ReservationUid, Ksenia));

        Assert.Equal(1, _retryQueue.Count);
        Assert.Contains("POST /api/v1/loyalty/decrement", _loyalty.Requests);

        _payment.IsStopped = false;
        await _retryQueue.ProcessAsync();

        Assert.Equal(0, _retryQueue.Count);
        Assert.Equal($"DELETE /api/v1/payments/{PaymentUid}", _payment.Requests[^1]);
    }

    [Fact]
    public async Task Cancel_UnknownReservation_NotFound()
    {
        var result = await _gateway.CancelReservation(Guid.Parse("00000000-0000-4000-8000-000000000404"), Ksenia);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(new ErrorResponse("Reservation not found"), notFound.Value);
    }
}
