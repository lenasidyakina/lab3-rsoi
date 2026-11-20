using GatewayService.Models;
using GatewayService.Resilience;

namespace GatewayService.Clients;

public class LoyaltyClient(HttpClient http, ServiceBreakers breakers)
{
    private const string Service = ServiceBreakers.LoyaltyName;

    public Task<LoyaltyInfoResponse> GetAsync(string username) =>
        breakers.Loyalty.ExecuteAsync(() => SendAsync(HttpMethod.Get, "/api/v1/loyalty", username));

    public Task<LoyaltyInfoResponse> IncrementAsync(string username) =>
        ServiceFailure.Wrap(Service, () => SendAsync(HttpMethod.Post, "/api/v1/loyalty/increment", username));

    public Task<LoyaltyInfoResponse> DecrementAsync(string username) =>
        ServiceFailure.Wrap(Service, () => SendAsync(HttpMethod.Post, "/api/v1/loyalty/decrement", username));

    private async Task<LoyaltyInfoResponse> SendAsync(HttpMethod method, string url, string username)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-User-Name", username);

        using var response = await http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoyaltyInfoResponse>())!;
    }
}
