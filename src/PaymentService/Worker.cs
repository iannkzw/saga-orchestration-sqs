using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Shared.Configuration;
using Shared.Contracts.Commands;
using Shared.Contracts.Replies;
using Shared.Idempotency;

namespace PaymentService;

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
        _logger.LogInformation("PaymentService worker started");

        await _idempotencyStore.EnsureTableAsync();

        var commandQueueUrl = (await _sqs.GetQueueUrlAsync(SqsConfig.PaymentCommands, stoppingToken)).QueueUrl;
        var replyQueueUrl = (await _sqs.GetQueueUrlAsync(SqsConfig.PaymentReplies, stoppingToken)).QueueUrl;

        _logger.LogInformation("Filas resolvidas: commands={CommandQueue}, replies={ReplyQueue}", commandQueueUrl, replyQueueUrl);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = commandQueueUrl,
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
                            : "ProcessPayment";

                        if (commandType == nameof(RefundPayment))
                        {
                            await HandleRefundPaymentAsync(message, replyQueueUrl, stoppingToken);
                        }
                        else
                        {
                            await HandleProcessPaymentAsync(message, replyQueueUrl, stoppingToken);
                        }

                        await _sqs.DeleteMessageAsync(commandQueueUrl, message.ReceiptHandle, stoppingToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogError(ex, "Erro ao processar mensagem: {MessageId}", message.MessageId);
                    }
                }

                await Task.Delay(500, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("PaymentService worker stopped");
    }

    private async Task HandleProcessPaymentAsync(Message message, string replyQueueUrl, CancellationToken ct)
    {
        var command = JsonSerializer.Deserialize<ProcessPayment>(message.Body)!;

        _logger.LogInformation(
            "Comando recebido: ProcessPayment SagaId={SagaId}, OrderId={OrderId}, Amount={Amount}",
            command.SagaId, command.OrderId, command.Amount);

        // Verificar idempotencia
        var cachedReply = await _idempotencyStore.TryGetAsync<PaymentReply>(command.IdempotencyKey);
        if (cachedReply is not null)
        {
            _logger.LogInformation("Idempotency hit para ProcessPayment IdempotencyKey={IdempotencyKey}, SagaId={SagaId}",
                command.IdempotencyKey, command.SagaId);

            await _sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = replyQueueUrl,
                MessageBody = JsonSerializer.Serialize(cachedReply)
            }, ct);

            return;
        }

        // Verificar simulacao de falha
        var shouldFail = message.MessageAttributes.TryGetValue("SimulateFailure", out var failAttr)
            && failAttr.StringValue.Equals("payment", StringComparison.OrdinalIgnoreCase);

        await Task.Delay(200, ct);

        var reply = new PaymentReply
        {
            SagaId = command.SagaId,
            Success = !shouldFail,
            TransactionId = shouldFail ? null : Guid.NewGuid().ToString(),
            ErrorMessage = shouldFail ? "Falha simulada no pagamento" : null
        };

        await _idempotencyStore.SaveAsync(command.IdempotencyKey, command.SagaId, reply);

        await _sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = replyQueueUrl,
            MessageBody = JsonSerializer.Serialize(reply)
        }, ct);

        _logger.LogInformation(
            "Reply enviado: PaymentReply SagaId={SagaId}, Success={Success}, TransactionId={TransactionId}",
            reply.SagaId, reply.Success, reply.TransactionId);
    }

    private async Task HandleRefundPaymentAsync(Message message, string replyQueueUrl, CancellationToken ct)
    {
        var command = JsonSerializer.Deserialize<RefundPayment>(message.Body)!;

        _logger.LogInformation(
            "Comando de compensacao: RefundPayment SagaId={SagaId}, OrderId={OrderId}, Amount={Amount}, TransactionId={TransactionId}",
            command.SagaId, command.OrderId, command.Amount, command.TransactionId);

        // Verificar idempotencia
        var cachedReply = await _idempotencyStore.TryGetAsync<RefundPaymentReply>(command.IdempotencyKey);
        if (cachedReply is not null)
        {
            _logger.LogInformation("Idempotency hit para RefundPayment IdempotencyKey={IdempotencyKey}, SagaId={SagaId}",
                command.IdempotencyKey, command.SagaId);

            await _sqs.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = replyQueueUrl,
                MessageBody = JsonSerializer.Serialize(cachedReply)
            }, ct);

            return;
        }

        await Task.Delay(200, ct);

        var reply = new RefundPaymentReply
        {
            SagaId = command.SagaId,
            Success = true,
            RefundId = $"REFUND-{Guid.NewGuid().ToString()[..8].ToUpperInvariant()}"
        };

        await _idempotencyStore.SaveAsync(command.IdempotencyKey, command.SagaId, reply);

        await _sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = replyQueueUrl,
            MessageBody = JsonSerializer.Serialize(reply)
        }, ct);

        _logger.LogInformation(
            "Reply de compensacao enviado: RefundPaymentReply SagaId={SagaId}, RefundId={RefundId}",
            reply.SagaId, reply.RefundId);
    }
}
