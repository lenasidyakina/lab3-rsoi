using System.Net;
using System.Net.Http.Json;

namespace HotelsBooking.Tests;

internal sealed class FakeService(string host) : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string Path, Func<HttpResponseMessage> Reply)> _routes = [];

    public bool IsStopped { get; set; }
    public List<string> Requests { get; } = [];

    public FakeService On(HttpMethod method, string path, object? body, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes.Add((method, path, () => new HttpResponseMessage(status)
        {
            Content = body is null ? null : JsonContent.Create(body)
        }));
        return this;
    }

    public HttpClient CreateClient() => new(this, disposeHandler: false) { BaseAddress = new Uri($"http://{host}") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
        if (IsStopped)
            throw new HttpRequestException($"Name or service not known ({host})");

        var route = _routes.LastOrDefault(r => r.Method == request.Method && r.Path == request.RequestUri.AbsolutePath);
        return Task.FromResult(route.Reply is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : route.Reply());
    }
}
