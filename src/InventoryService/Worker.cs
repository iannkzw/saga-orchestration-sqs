using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Shared.Configuration;
using Shared.Contracts.Commands;
using Shared.Contracts.Replies;
using Shared.Idempotency;
using Shared.Telemetry;

namespace InventoryService;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IAmazonSQS _sqs;
    private readonly IdempotencyStore _idempotencyStore;
    private readonly InventoryRepository _inventoryRepository;
    private readonly bool _lockingEnabled;

    public Worker(
        ILogger<Worker> logger,
        IAmazonSQS sqs,
        IdempotencyStore idempotencyStore,
        InventoryRepository inventoryRepository,
        IConfiguration configuration)
    {
        _logger = logger;
        _sqs = sqs;
        _idempotencyStore = idempotencyStore;
        _inventoryRepository = inventoryRepository;
        // INVENTORY_LOCKING_ENABLED=true  → SELECT FOR UPDATE (padrao, comportamento correto)
        // INVENTORY_LOCKING_ENABLED=false → sem lock (demonstra race condition / overbooking)
        _lockingEnabled = configuration.GetValue<bool>("INVENTORY_LOCKING_ENABLED", true);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "InventoryService worker iniciado — locking={LockMode}",
            _lockingEnabled ? "FOR UPDATE (pessimista)" : "SEM LOCK (demonstracao de race condition)");

        var commandsQueueUrl = (await _sqs.GetQueueUrlAsync(SqsConfig.InventoryCommands, stoppingToken)).QueueUrl;
        var repliesQueueUrl = (await _sqs.GetQueueUrlAsync(SqsConfig.InventoryReplies, stoppingToken)).QueueUrl;

        _logger.LogInformation("Filas resolvidas — commands: {CommandsQueue}, replies: {RepliesQueue}",
            commandsQueueUrl, repliesQueueUrl);

        await _idempotencyStore.EnsureTableAsync();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var response = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = commandsQueueUrl,
                    WaitTimeSeconds = 2,
                    MaxNumberOfMessages = 10,
                    MessageAttributeNames = ["All"]
                }, stoppingToken);

                if (response.Messages.Count > 0)
                {
                    // Processar mensagens em paralelo para expor race conditions quando locking=false.
                    // Com FOR UPDATE o PostgreSQL serializa os locks — sem lock, transacoes concorrentes
                    // podem ler o mesmo estoque e todas aprovarem (overbooking).
                    var tasks = response.Messages.Select(message => ProcessMessageAsync(
                        message, commandsQueueUrl, repliesQueueUrl, stoppingToken));
                    await Task.WhenAll(tasks);
                }

                await Task.Delay(200, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("InventoryService worker encerrando graciosamente");
        }
    }

    private async Task ProcessMessageAsync(
        Message message, string commandsQueueUrl, string repliesQueueUrl, CancellationToken ct)
    {
        try
        {
            var commandType = message.MessageAttributes.TryGetValue("CommandType", out var attr)
                ? attr.StringValue
                : "ReserveInventory";

            if (commandType == nameof(ReleaseInventory))
                await HandleReleaseInventoryAsync(message, repliesQueueUrl, ct);
            else
                await HandleReserveInventoryAsync(message, repliesQueueUrl, ct);

            await _sqs.DeleteMessageAsync(commandsQueueUrl, message.ReceiptHandle, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Erro ao processar mensagem: {MessageId}", message.MessageId);
        }
    }

    private async Task HandleReserveInventoryAsync(Message message, string repliesQueueUrl, CancellationToken ct)
    {
        var command = JsonSerializer.Deserialize<ReserveInventory>(message.Body)!;
        var parentContext = SqsTracePropagation.Extract(message.MessageAttributes);
        using var processActivity = SagaActivitySource.StartProcessCommand(
            nameof(ReserveInventory), command.SagaId.ToString(), parentContext.ActivityContext);

        _logger.LogInformation(
            "Comando recebido: ReserveInventory SagaId={SagaId}, OrderId={OrderId}, Items={ItemsCount}",
            command.SagaId, command.OrderId, command.Items.Count);

        // Verificar idempotencia antes de qualquer processamento
        var cached = await _idempotencyStore.TryGetAsync<InventoryReply>(command.IdempotencyKey);
        if (cached is not null)
        {
            _logger.LogInformation(
                "Idempotency hit para ReserveInventory IdempotencyKey={Key}, SagaId={SagaId}",
                command.IdempotencyKey, command.SagaId);
            await SendReplyAsync(repliesQueueUrl, cached, ct);
            return;
        }

        bool success;
        string? reservationId = null;
        string? errorMessage;

        var firstItem = command.Items.FirstOrDefault();
        if (firstItem is null)
        {
            success = false;
            errorMessage = "Nenhum item no pedido";
        }
        else
        {
            var newReservationId = Guid.NewGuid().ToString();
            // useLock=true  → SELECT FOR UPDATE: PostgreSQL serializa o acesso, sem overbooking
            // useLock=false → SELECT sem lock + delay: expoe a janela TOCTOU com processamento paralelo
            var (ok, err) = await _inventoryRepository.TryReserveAsync(
                firstItem.ProductId, firstItem.Quantity, newReservationId, command.SagaId,
                useLock: _lockingEnabled, ct);
            success = ok;
            reservationId = ok ? newReservationId : null;
            errorMessage = err;
        }

        var reply = new InventoryReply
        {
            SagaId = command.SagaId,
            Success = success,
            ReservationId = reservationId,
            ErrorMessage = errorMessage
        };

        await _idempotencyStore.SaveAsync(command.IdempotencyKey, command.SagaId, reply);
        await SendReplyAsync(repliesQueueUrl, reply, ct);

        _logger.LogInformation(
            "Reply enviado: InventoryReply SagaId={SagaId}, Success={Success}, ReservationId={ReservationId}",
            reply.SagaId, reply.Success, reply.ReservationId);
    }

    private async Task HandleReleaseInventoryAsync(Message message, string repliesQueueUrl, CancellationToken ct)
    {
        var command = JsonSerializer.Deserialize<ReleaseInventory>(message.Body)!;
        var parentContext = SqsTracePropagation.Extract(message.MessageAttributes);
        using var processActivity = SagaActivitySource.StartProcessCommand(
            nameof(ReleaseInventory), command.SagaId.ToString(), parentContext.ActivityContext);

        _logger.LogInformation(
            "Comando de compensacao: ReleaseInventory SagaId={SagaId}, ReservationId={ReservationId}",
            command.SagaId, command.ReservationId);

        // Verificar idempotencia
        var cached = await _idempotencyStore.TryGetAsync<ReleaseInventoryReply>(command.IdempotencyKey);
        if (cached is not null)
        {
            _logger.LogInformation(
                "Idempotency hit para ReleaseInventory IdempotencyKey={Key}, SagaId={SagaId}",
                command.IdempotencyKey, command.SagaId);
            await SendReplyAsync(repliesQueueUrl, cached, ct);
            return;
        }

        bool released;
        if (!string.IsNullOrEmpty(command.ReservationId))
        {
            released = await _inventoryRepository.ReleaseAsync(command.ReservationId, ct);
        }
        else
        {
            // Saga falhou antes de reservar — nada a liberar
            _logger.LogWarning(
                "ReleaseInventory sem ReservationId — saga falhou antes de reservar. SagaId={SagaId}",
                command.SagaId);
            released = true;
        }

        var reply = new ReleaseInventoryReply
        {
            SagaId = command.SagaId,
            Success = released,
            ReservationId = command.ReservationId
        };

        await _idempotencyStore.SaveAsync(command.IdempotencyKey, command.SagaId, reply);
        await SendReplyAsync(repliesQueueUrl, reply, ct);

        _logger.LogInformation(
            "Reply de compensacao: ReleaseInventoryReply SagaId={SagaId}, Success={Success}",
            reply.SagaId, reply.Success);
    }

    private async Task SendReplyAsync<T>(string repliesQueueUrl, T reply, CancellationToken ct)
    {
        using (SagaActivitySource.StartSendReply(typeof(T).Name, string.Empty))
        {
            var request = new SendMessageRequest
            {
                QueueUrl = repliesQueueUrl,
                MessageBody = JsonSerializer.Serialize(reply),
                MessageAttributes = new Dictionary<string, MessageAttributeValue>()
            };
            SqsTracePropagation.Inject(request.MessageAttributes);
            await _sqs.SendMessageAsync(request, ct);
        }
    }
}
