using System.Net.Http.Json;
using System.Text.Json;
using IntegrationTests.Models;

namespace IntegrationTests.Infrastructure;

public sealed class SagaClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _orderClient;

    public SagaClient(string orderServiceUrl = "http://localhost:5001")
    {
        _orderClient = new HttpClient { BaseAddress = new Uri(orderServiceUrl) };
    }

    /// <summary>
    /// POST /orders → retorna (orderId, sagaId).
    /// </summary>
    public async Task<(Guid OrderId, Guid SagaId)> PostOrderAsync(
        CreateOrderRequest request,
        string? simulateFailure = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/orders")
        {
            Content = JsonContent.Create(request)
        };

        if (!string.IsNullOrEmpty(simulateFailure))
            req.Headers.Add("X-Simulate-Failure", simulateFailure);

        var response = await _orderClient.SendAsync(req);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);
        var orderId = body.GetProperty("orderId").GetGuid();
        var sagaId = body.GetProperty("sagaId").GetGuid();

        return (orderId, sagaId);
    }

    /// <summary>
    /// GET /sagas/{sagaId} → retorna SagaResponse.
    /// </summary>
    public async Task<SagaResponse> GetSagaAsync(Guid sagaId)
    {
        var response = await _orderClient.GetAsync($"/sagas/{sagaId}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        return new SagaResponse(
            SagaId: body.GetProperty("sagaId").GetGuid(),
            OrderId: body.GetProperty("orderId").GetGuid(),
            State: body.GetProperty("state").GetString() ?? string.Empty,
            CreatedAt: body.GetProperty("createdAt").GetDateTime(),
            UpdatedAt: body.GetProperty("updatedAt").GetDateTime()
        );
    }

    /// <summary>
    /// Faz polling até a saga atingir estado terminal (Completed ou Failed).
    /// Lança TimeoutException se o estado terminal não for atingido no prazo.
    /// </summary>
    public async Task<SagaResponse> WaitForTerminalStateAsync(
        Guid sagaId,
        TimeSpan? timeout = null,
        TimeSpan? interval = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var effectiveInterval = interval ?? TimeSpan.FromMilliseconds(500);

        using var cts = new CancellationTokenSource(effectiveTimeout);

        SagaResponse? last = null;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                last = await GetSagaAsync(sagaId);
                if (last.State is "Final" or "Completed" or "Failed")
                    return last;
            }
            catch when (!cts.IsCancellationRequested)
            {
                // saga ainda não existe ou serviço temporariamente indisponível
            }

            await Task.Delay(effectiveInterval, cts.Token).ConfigureAwait(false);
        }

        var lastState = last?.State ?? "desconhecido";
        throw new TimeoutException(
            $"Saga {sagaId} não atingiu estado terminal em {effectiveTimeout.TotalSeconds}s. " +
            $"Último estado: {lastState}");
    }

    /// <summary>
    /// GET /orders/{orderId} → retorna OrderResponse.
    /// </summary>
    public async Task<OrderResponse> GetOrderAsync(Guid orderId)
    {
        var response = await _orderClient.GetAsync($"/orders/{orderId}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        return new OrderResponse(
            OrderId: body.GetProperty("orderId").GetGuid(),
            SagaId: body.TryGetProperty("sagaId", out var sid) && sid.ValueKind != JsonValueKind.Null
                ? sid.GetGuid() : null,
            Status: body.GetProperty("status").GetString() ?? string.Empty,
            TotalAmount: body.GetProperty("totalAmount").GetDecimal()
        );
    }

    /// <summary>
    /// Faz polling até order.status atingir o valor esperado.
    /// </summary>
    public async Task<OrderResponse> WaitForOrderStatusAsync(
        Guid orderId,
        string expectedStatus,
        TimeSpan? timeout = null,
        TimeSpan? interval = null)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(30);
        var effectiveInterval = interval ?? TimeSpan.FromMilliseconds(500);
        using var cts = new CancellationTokenSource(effectiveTimeout);

        OrderResponse? last = null;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                last = await GetOrderAsync(orderId);
                if (last.Status == expectedStatus)
                    return last;
            }
            catch when (!cts.IsCancellationRequested) { }

            await Task.Delay(effectiveInterval, cts.Token).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Order {orderId} não atingiu status '{expectedStatus}' em {effectiveTimeout.TotalSeconds}s. " +
            $"Último status: {last?.Status ?? "desconhecido"}");
    }

    public void Dispose()
    {
        _orderClient.Dispose();
    }
}
