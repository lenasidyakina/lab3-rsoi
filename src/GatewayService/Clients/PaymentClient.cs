using GatewayService.Models;
using GatewayService.Resilience;

namespace GatewayService.Clients;

public class PaymentClient(HttpClient http, ServiceBreakers breakers)
{
    private const string Service = ServiceBreakers.PaymentName;

    public Task<PaymentDto> GetAsync(Guid paymentUid) =>
        breakers.Payment.ExecuteAsync(async () =>
            (await http.GetFromJsonAsync<PaymentDto>($"/api/v1/payments/{paymentUid}"))!);

    public Task<PaymentDto> CreateAsync(int price) =>
        ServiceFailure.Wrap(Service, async () =>
        {
            using var response = await http.PostAsJsonAsync("/api/v1/payments", new { price });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<PaymentDto>())!;
        });

    public Task CancelAsync(Guid paymentUid) =>
        ServiceFailure.Wrap(Service, async () =>
        {
            using var response = await http.DeleteAsync($"/api/v1/payments/{paymentUid}");
            response.EnsureSuccessStatusCode();
        });
}
