using System.Diagnostics;
using System.Reflection;

namespace IntegrationTests.Infrastructure;

/// <summary>
/// Fixture que sobe o ambiente Docker Compose antes de todos os testes e derruba ao finalizar.
/// Compartilhada entre todos os testes via ICollectionFixture (sobe uma vez por suite).
/// </summary>
public sealed class DockerComposeFixture : IAsyncLifetime
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string DockerComposeFile = Path.Combine(RepoRoot, "docker-compose.yml");
    private static readonly string TestOverrideFile = Path.Combine(RepoRoot, "tests", "IntegrationTests", "docker-compose.test.yml");

    private static readonly (string Name, string Url)[] ServiceHealthEndpoints =
    [
        ("order-service",      "http://localhost:5001/health"),
        ("payment-service",    "http://localhost:5003/health"),
        ("inventory-service",  "http://localhost:5004/health"),
        ("shipping-service",   "http://localhost:5005/health"),
    ];

    // Filas SQS que devem existir antes dos serviços iniciarem.
    // Com MassTransit as filas são criadas pelos consumers via ConfigureEndpoints;
    // aqui aguardamos as filas legadas ainda criadas pelo init-sqs.sh.
    private static readonly string[] RequiredQueues =
    [
        "order-commands", "payment-commands", "inventory-commands", "shipping-commands"
    ];

    private const string LocalStackSqsUrl = "http://localhost:4566";

    // Timeout generoso: primeiro build das imagens pode levar >3min
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(360);

    private readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    public async Task InitializeAsync()
    {
        // Build separado: não bloqueia o up enquanto compila
        await RunDockerComposeAsync("build");
        await RunDockerComposeAsync("up -d");

        // Aguarda as filas SQS serem criadas pelo init-sqs.sh (roda async após LocalStack healthy)
        // Só depois disso os serviços conseguem resolver GetQueueUrlAsync sem crash
        await WaitForSqsQueuesAsync(HealthTimeout);

        // Aguarda os serviços HTTP ficarem healthy (podem reiniciar via on-failure enquanto filas não existiam)
        await WaitForAllServicesAsync(HealthTimeout);

        // Aguarda estabilização: health pode passar antes das tabelas estarem prontas
        await Task.Delay(TimeSpan.FromSeconds(5));

        // Reset de estoque padrão para testes de compensação/happy path (com retry)
        using var inventoryClient = new InventoryClient();
        await RetryAsync(() => inventoryClient.ResetStockAsync("PROD-001", 100), maxAttempts: 10, delayMs: 3000);
    }

    public async Task DisposeAsync()
    {
        await RunDockerComposeAsync("down -v");
        _httpClient.Dispose();
    }

    private static Task RunDockerComposeAsync(string subcommand)
    {
        var args = $"compose -f \"{DockerComposeFile}\" -f \"{TestOverrideFile}\" {subcommand}";
        var psi = new ProcessStartInfo("docker", args)
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            UseShellExecute = false
        };

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Falha ao iniciar: docker {args}");

        return process.WaitForExitAsync();
    }

    private async Task WaitForSqsQueuesAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        while (!cts.IsCancellationRequested)
        {
            try
            {
                var response = await _httpClient.GetAsync(
                    $"{LocalStackSqsUrl}/000000000000?Action=ListQueues", cts.Token);

                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync(cts.Token);
                    var allPresent = RequiredQueues.All(q => body.Contains(q));
                    if (allPresent)
                        return;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // LocalStack ainda não está up
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        throw new TimeoutException(
            $"Filas SQS não foram criadas em {timeout.TotalSeconds}s. Verifique o init-sqs.sh do LocalStack.");
    }

    private async Task WaitForAllServicesAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);

        var tasks = ServiceHealthEndpoints
            .Select(svc => WaitForHealthAsync(svc.Url, cts.Token))
            .ToList();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            // Identifica qual serviço não ficou healthy
            var notHealthy = ServiceHealthEndpoints
                .Where((svc, i) => tasks[i].IsFaulted || tasks[i].IsCanceled)
                .Select(svc => svc.Name)
                .ToList();

            throw new TimeoutException(
                $"Serviços não ficaram healthy em {timeout.TotalSeconds}s: {string.Join(", ", notHealthy)}");
        }
    }

    private async Task WaitForHealthAsync(string url, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var response = await _httpClient.GetAsync(url, ct);
                if (response.IsSuccessStatusCode)
                    return;
            }
            catch (OperationCanceledException)
            {
                // Deixa propagar — é timeout real
                throw;
            }
            catch
            {
                // Serviço ainda não está pronto, tentar novamente
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }
    }

    private static async Task RetryAsync(Func<Task> action, int maxAttempts, int delayMs = 2000)
    {
        for (int i = 1; i <= maxAttempts; i++)
        {
            try
            {
                await action();
                return;
            }
            catch when (i < maxAttempts)
            {
                await Task.Delay(delayMs);
            }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
            ?? throw new InvalidOperationException("Não foi possível determinar o diretório do assembly");

        var current = new DirectoryInfo(dir);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "docker-compose.yml")))
                return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException("Raiz do repositório não encontrada (docker-compose.yml ausente)");
    }
}
