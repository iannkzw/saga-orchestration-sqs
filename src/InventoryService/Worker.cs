using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Shared.Configuration;
using Shared.Contracts.Commands;
using Shared.Contracts.Replies;
using Shared.Idempotency;

namespace InventoryService;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IAmazonSQS _sqs;
    private readonly IdempotencyStore _idempotencyStore;

    public Worker(ILogger<Worker> logger, IAmazonSQS sqs, IdempotencyStore idempotencyStore)
    {
        _logger = logger;
        _sqs = sqs;
        _idempotencyStore = idempotencyStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("InventoryService worker started");

        var commandsQueueUrl = (await _sqs.GetQueueUrlAsync(SqsConfig.InventoryCommands, stoppingToken)).QueueUrl;
        var repliesQueueUrl = (await _sqs.GetQueueUrlAsync(SqsConfig.InventoryReplies, stoppingToken)).QueueUrl;

        _logger.LogInformation("Queues resolved — commands: {CommandsQueue}, replies: {RepliesQueue}", commandsQueueUrl, repliesQueueUrl);

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

                foreach (var message in response.Messages)
                {
                    try
                    {
                        var commandType = message.MessageAttributes.TryGetValue("CommandType", out var attr)
                            ? attr.StringValue
                            : "ReserveInventory";

                        if (commandType == nameof(ReleaseInventory))
                        {
                            await HandleReleaseInventoryAsync(message, repliesQueueUrl, stoppingToken);
                        }
                        else
                        {
                            await HandleReserveInventoryAsync(message, repliesQueueUrl, stoppingToken);
                        }

                        await _sqs.DeleteMessageAsync(commandsQueueUrl, message.ReceiptHandle, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Erro ao processar mensagem: {MessageId}", message.MessageId);
                    }
                }

                await Task.Delay(500, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("InventoryService worker stopping gracefully");
        }
    }

    private async Task HandleReserveInventoryAsync(Message message, string repliesQueueUrl, CancellationToken ct)
    {
        var command = JsonSerializer.Deserialize<ReserveInventory>(message.Body)!;

        _logger.LogInformation(
            "Comando recebido: ReserveInventory SagaId={SagaId}, OrderId={OrderId}, Items={ItemsCount}",
            command.SagaId, command.OrderId, command.Items.Count);

        // Verificar idempotencia
        var cached = await _idempotencyStore.TryGetAsync<InventoryReply>(command.IdempotencyKey);
        if (cached is not null)
        {
            _logger.LogInformation("Idempotency hit para ReserveInventory IdempotencyKey={IdempotencyKey}, SagaId={SagaId}",
                command.IdempotencyKey, command.SagaId);

            await _sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = repliesQueueUrl,
                MessageBody = JsonSerializer.Serialize(cached)
            }, ct);

            return;
        }

        // Verificar simulacao de falha
        var shouldFail = message.MessageAttributes.TryGetValue("SimulateFailure", out var failAttr)
            && failAttr.StringValue.Equals("inventory", StringComparison.OrdinalIgnoreCase);

        await Task.Delay(200, ct);

        var reply = new InventoryReply
        {
            SagaId = command.SagaId,
            Success = !shouldFail,
            ReservationId = shouldFail ? null : Guid.NewGuid().ToString(),
            ErrorMessage = shouldFail ? "Falha simulada na reserva de inventario" : null
        };

        await _idempotencyStore.SaveAsync(command.IdempotencyKey, command.SagaId, reply);

        await _sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = repliesQueueUrl,
            MessageBody = JsonSerializer.Serialize(reply)
        }, ct);

        _logger.LogInformation(
            "Reply enviado: InventoryReply SagaId={SagaId}, Success={Success}, ReservationId={ReservationId}",
            reply.SagaId, reply.Success, reply.ReservationId);
    }

    private async Task HandleReleaseInventoryAsync(Message message, string repliesQueueUrl, CancellationToken ct)
    {
        var command = JsonSerializer.Deserialize<ReleaseInventory>(message.Body)!;

        _logger.LogInformation(
            "Comando de compensacao: ReleaseInventory SagaId={SagaId}, OrderId={OrderId}, ReservationId={ReservationId}",
            command.SagaId, command.OrderId, command.ReservationId);

        // Verificar idempotencia
        var cached = await _idempotencyStore.TryGetAsync<ReleaseInventoryReply>(command.IdempotencyKey);
        if (cached is not null)
        {
            _logger.LogInformation("Idempotency hit para ReleaseInventory IdempotencyKey={IdempotencyKey}, SagaId={SagaId}",
                command.IdempotencyKey, command.SagaId);

            await _sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = repliesQueueUrl,
                MessageBody = JsonSerializer.Serialize(cached)
            }, ct);

            return;
        }

        await Task.Delay(200, ct);

        var reply = new ReleaseInventoryReply
        {
            SagaId = command.SagaId,
            Success = true,
            ReservationId = command.ReservationId
        };

        await _idempotencyStore.SaveAsync(command.IdempotencyKey, command.SagaId, reply);

        await _sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = repliesQueueUrl,
            MessageBody = JsonSerializer.Serialize(reply)
        }, ct);

        _logger.LogInformation(
            "Reply de compensacao enviado: ReleaseInventoryReply SagaId={SagaId}, ReservationId={ReservationId}",
            reply.SagaId, reply.ReservationId);
    }
}
