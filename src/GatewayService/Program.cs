using GatewayService.Clients;
using GatewayService.Resilience;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers(o => o.Filters.Add<ServiceUnavailableFilter>());

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<CircuitBreakerOptions>(builder.Configuration.GetSection("CircuitBreaker"));
builder.Services.Configure<RetryQueueOptions>(builder.Configuration.GetSection("RetryQueue"));
builder.Services.AddSingleton<ServiceBreakers>();
builder.Services.AddSingleton<RetryQueue>();
builder.Services.AddHostedService<RetryQueueWorker>();

var timeout = builder.Configuration.GetValue("Services:Timeout", TimeSpan.FromSeconds(2));

builder.Services.AddHttpClient<ReservationClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Services:Reservation"]!);
    c.Timeout = timeout;
});
builder.Services.AddHttpClient<PaymentClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Services:Payment"]!);
    c.Timeout = timeout;
});
builder.Services.AddHttpClient<LoyaltyClient>(c =>
{
    c.BaseAddress = new Uri(builder.Configuration["Services:Loyalty"]!);
    c.Timeout = timeout;
});

var app = builder.Build();

app.MapGet("/manage/health", () => Results.Ok());
app.MapControllers();

app.Run();
