using GatewayService.Clients;
using GatewayService.Domain;
using GatewayService.Models;
using GatewayService.Resilience;
using Microsoft.AspNetCore.Mvc;

namespace GatewayService.Controllers;

[ApiController]
[Route("api/v1")]
public class GatewayController(
    ReservationClient reservationClient,
    PaymentClient paymentClient,
    LoyaltyClient loyaltyClient,
    RetryQueue retryQueue,
    ILogger<GatewayController> logger) : ControllerBase
{
    // Reservation Service — критичный источник: при недоступности 503 (ServiceUnavailableFilter)
    [HttpGet("hotels")]
    public async Task<ActionResult<PaginationResponse>> GetHotels([FromQuery] int page = 1, [FromQuery] int size = 10)
    {
        return Ok(await reservationClient.GetHotelsAsync(page, size));
    }

    [HttpGet("me")]
    public async Task<ActionResult<UserInfoResponse>> GetMe([FromHeader(Name = "X-User-Name")] string username)
    {
        var reservations = await GetUserReservationsAsync(username);
        var loyalty = await GetLoyaltyOrFallbackAsync(username);
        return Ok(new UserInfoResponse(reservations, loyalty));
    }

    [HttpGet("reservations")]
    public async Task<ActionResult<List<ReservationResponse>>> GetReservations(
        [FromHeader(Name = "X-User-Name")] string username)
    {
        return Ok(await GetUserReservationsAsync(username));
    }

    [HttpGet("reservations/{reservationUid:guid}")]
    public async Task<ActionResult<ReservationResponse>> GetReservation(
        Guid reservationUid, [FromHeader(Name = "X-User-Name")] string username)
    {
        var reservation = await reservationClient.GetReservationAsync(reservationUid, username);
        if (reservation is null)
            return NotFound(new ErrorResponse("Reservation not found"));

        return Ok(ToResponse(reservation, await GetPaymentOrFallbackAsync(reservation.PaymentUid)));
    }

    [HttpPost("reservations")]
    public async Task<ActionResult<CreateReservationResponse>> CreateReservation(
        [FromHeader(Name = "X-User-Name")] string username, [FromBody] CreateReservationRequest request)
    {
        if (request.EndDate <= request.StartDate)
            return BadRequest(new ValidationErrorResponse("Validation failed",
                [new ErrorDescription("endDate", "endDate must be after startDate")]));

        // 1. Проверка отеля (Reservation Service недоступен → ошибка)
        var hotel = await reservationClient.GetHotelAsync(request.HotelUid);
        if (hotel is null)
            return NotFound(new ErrorResponse("Hotel not found"));

        // 2. Скидка из Loyalty Service (недоступен → ошибка, ничего ещё не изменено)
        await retryQueue.ProcessAsync();
        var loyalty = await loyaltyClient.GetAsync(username);
        var price = Pricing.Calculate(hotel.Price, Pricing.Nights(request.StartDate, request.EndDate), loyalty.Discount);

        // 3. Создание оплаты (Payment Service недоступен → ошибка)
        var payment = await paymentClient.CreateAsync(price);

        // 4. Увеличение счётчика бронирований; при ошибке — откат оплаты
        try
        {
            await loyaltyClient.IncrementAsync(username);
        }
        catch (ServiceUnavailableException)
        {
            await RollbackPaymentAsync(payment.PaymentUid);
            throw;
        }

        // 5. Создание бронирования; при ошибке — откат оплаты и счётчика лояльности
        ReservationDto reservation;
        try
        {
            reservation = await reservationClient.CreateReservationAsync(username,
                new CreateReservationDto(request.HotelUid, payment.PaymentUid, request.StartDate, request.EndDate));
        }
        catch (ServiceUnavailableException)
        {
            await RollbackPaymentAsync(payment.PaymentUid);
            await DecrementLoyaltyOrEnqueueAsync(username);
            throw;
        }

        return Ok(new CreateReservationResponse(
            reservation.ReservationUid,
            hotel.HotelUid,
            reservation.StartDate,
            reservation.EndDate,
            loyalty.Discount,
            reservation.Status,
            new PaymentInfo(payment.Status, payment.Price)));
    }

    [HttpDelete("reservations/{reservationUid:guid}")]
    public async Task<IActionResult> CancelReservation(
        Guid reservationUid, [FromHeader(Name = "X-User-Name")] string username)
    {
        var reservation = await reservationClient.GetReservationAsync(reservationUid, username);
        if (reservation is null)
            return NotFound(new ErrorResponse("Reservation not found"));

        await reservationClient.CancelReservationAsync(reservationUid, username);
        await paymentClient.CancelAsync(reservation.PaymentUid);

        // Loyalty Service недоступен → пользователю всё равно успех, запрос уходит в очередь на повтор
        await DecrementLoyaltyOrEnqueueAsync(username);

        return NoContent();
    }

    [HttpGet("loyalty")]
    public async Task<ActionResult<LoyaltyInfoResponse>> GetLoyalty([FromHeader(Name = "X-User-Name")] string username)
    {
        await retryQueue.ProcessAsync();
        return Ok(await loyaltyClient.GetAsync(username));
    }

    private async Task<List<ReservationResponse>> GetUserReservationsAsync(string username)
    {
        var reservations = await reservationClient.GetReservationsAsync(username);
        var result = new List<ReservationResponse>();
        foreach (var reservation in reservations)
            result.Add(ToResponse(reservation, await GetPaymentOrFallbackAsync(reservation.PaymentUid)));
        return result;
    }

    private async Task<object> GetPaymentOrFallbackAsync(Guid paymentUid)
    {
        try
        {
            var p = await paymentClient.GetAsync(paymentUid);
            return new PaymentInfo(p.Status, p.Price);
        }
        catch (ServiceUnavailableException)
        {
            return EmptyObject.Instance;
        }
    }

    private async Task<object> GetLoyaltyOrFallbackAsync(string username)
    {
        try
        {
            await retryQueue.ProcessAsync();
            return await loyaltyClient.GetAsync(username);
        }
        catch (ServiceUnavailableException)
        {
            return EmptyObject.Instance;
        }
    }

    private async Task RollbackPaymentAsync(Guid paymentUid)
    {
        try
        {
            await paymentClient.CancelAsync(paymentUid);
            logger.LogInformation("Payment {PaymentUid} rolled back", paymentUid);
        }
        catch (ServiceUnavailableException)
        {
            retryQueue.Enqueue($"cancel payment {paymentUid}",
                sp => sp.GetRequiredService<PaymentClient>().CancelAsync(paymentUid));
        }
    }

    private async Task DecrementLoyaltyOrEnqueueAsync(string username)
    {
        try
        {
            await loyaltyClient.DecrementAsync(username);
        }
        catch (ServiceUnavailableException)
        {
            retryQueue.Enqueue($"loyalty decrement for {username}",
                sp => sp.GetRequiredService<LoyaltyClient>().DecrementAsync(username));
        }
    }

    private static ReservationResponse ToResponse(ReservationDto r, object payment) => new(
        r.ReservationUid,
        new HotelInfo(
            r.Hotel.HotelUid,
            r.Hotel.Name,
            $"{r.Hotel.Country}, {r.Hotel.City}, {r.Hotel.Address}",
            r.Hotel.Stars),
        r.StartDate,
        r.EndDate,
        r.Status,
        payment);
}
