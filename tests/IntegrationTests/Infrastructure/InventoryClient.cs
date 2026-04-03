using System.Net.Http.Json;
using System.Text.Json;
using IntegrationTests.Models;

namespace IntegrationTests.Infrastructure;

public sealed class InventoryClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;

    public InventoryClient(string inventoryServiceUrl = "http://localhost:5004")
    {
        _client = new HttpClient { BaseAddress = new Uri(inventoryServiceUrl) };
    }

    /// <summary>
    /// GET /inventory/stock/{productId} → retorna StockResponse.
    /// </summary>
    public async Task<StockResponse> GetStockAsync(string productId)
    {
        var response = await _client.GetAsync($"/inventory/stock/{productId}");
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions);

        return new StockResponse(
            ProductId: body.GetProperty("productId").GetString() ?? productId,
            Quantity: body.GetProperty("quantity").GetInt32()
        );
    }

    /// <summary>
    /// POST /inventory/reset → reseta estoque de um produto para a quantidade informada.
    /// </summary>
    public async Task ResetStockAsync(string productId, int quantity)
    {
        var response = await _client.PostAsJsonAsync(
            "/inventory/reset",
            new { productId, quantity });

        response.EnsureSuccessStatusCode();
    }

    public void Dispose() => _client.Dispose();
}
