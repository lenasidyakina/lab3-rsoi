using System.Net;
using GatewayService.Models;
using GatewayService.Resilience;

namespace GatewayService.Clients;

public class ReservationClient(HttpClient http, ServiceBreakers breakers)
{
    private const string Service = ServiceBreakers.ReservationName;
    private CircuitBreaker Breaker => breakers.Reservation;

    public Task<PaginationResponse> GetHotelsAsync(int page, int size) =>
        Breaker.ExecuteAsync(async () =>
            (await http.GetFromJsonAsync<PaginationResponse>($"/api/v1/hotels?page={page}&size={size}"))!);

    public Task<HotelResponse?> GetHotelAsync(Guid hotelUid) =>
        Breaker.ExecuteAsync(async () =>
        {
            using var response = await http.GetAsync($"/api/v1/hotels/{hotelUid}");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<HotelResponse>();
        });

    public Task<List<ReservationDto>> GetReservationsAsync(string username) =>
        Breaker.ExecuteAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/reservations");
            request.Headers.Add("X-User-Name", username);

            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<List<ReservationDto>>())!;
        });

    public Task<ReservationDto?> GetReservationAsync(Guid reservationUid, string username) =>
        Breaker.ExecuteAsync(async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/reservations/{reservationUid}");
            request.Headers.Add("X-User-Name", username);

            using var response = await http.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<ReservationDto>();
        });

    public Task<ReservationDto> CreateReservationAsync(string username, CreateReservationDto body) =>
        ServiceFailure.Wrap(Service, async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/reservations");
            request.Headers.Add("X-User-Name", username);
            request.Content = JsonContent.Create(body);

            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<ReservationDto>())!;
        });

    public Task CancelReservationAsync(Guid reservationUid, string username) =>
        ServiceFailure.Wrap(Service, async () =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/reservations/{reservationUid}");
            request.Headers.Add("X-User-Name", username);

            using var response = await http.SendAsync(request);
            response.EnsureSuccessStatusCode();
        });
}
