using System.Diagnostics;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Shared.Configuration;
using Shared.Contracts.Commands;
using Shared.Contracts.Replies;
using Shared.Idempotency;
using Shared.Telemetry;

namespace ShippingService;

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
        _logger.LogInformation("ShippingService worker started");

        var commandsQueueUrl = (await _sqs.GetQueueUrlAsync(SqsConfig.ShippingCommands, stoppingToken)).QueueUrl;
        var repliesQueueUrl = (await _sqs.GetQueueUrlAsync(SqsConfig.ShippingReplies, stoppingToken)).QueueUrl;

        await _idempotencyStore.EnsureTableAsync();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = commandsQueueUrl,
                    WaitTimeSeconds = 2,
                    MaxNumberOfMessages = 10,
                    MessageAttributeNames = ["All"]
                }, stoppingToken);

                foreach (var message in response?.Messages ?? [])
                {
                    try
                    {
                        var commandType = message.MessageAttributes.TryGetValue("CommandType", out var attr)
                            ? attr.StringValue
                            : "ScheduleShipping";

                        if (commandType == nameof(CancelShipping))
                        {
                            await HandleCancelShippingAsync(message, repliesQueueUrl, stoppingToken);
                        }
                        else
                        {
                            await HandleScheduleShippingAsync(message, repliesQueueUrl, stoppingToken);
                        }

                        await _sqs.DeleteMessageAsync(commandsQueueUrl, message.ReceiptHandle, stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
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

        _logger.LogInformation("ShippingService worker stopped");
    }

    private async Task HandleScheduleShippingAsync(Message message, string repliesQueueUrl, CancellationToken ct)
    {
        var command = JsonSerializer.Deserialize<ScheduleShipping>(message.Body)!;
        var parentContext = SqsTracePropagation.Extract(message.MessageAttributes);
        using var processActivity = SagaActivitySource.StartProcessCommand(
            nameof(ScheduleShipping), command.SagaId.ToString(), parentContext.ActivityContext);
        processActivity?.SetTag("saga.order_id", command.OrderId.ToString());
        processActivity?.SetTag("shipping.address", command.ShippingAddress);

        // Verificar idempotencia
        var cachedReply = await _idempotencyStore.TryGetAsync<ShippingReply>(command.IdempotencyKey);
        if (cachedReply is not null)
        {
            _logger.LogInformation(
                "Idempotency hit: ScheduleShipping SagaId={SagaId}, IdempotencyKey={IdempotencyKey}",
                command.SagaId, command.IdempotencyKey);

            using (SagaActivitySource.StartSendReply(nameof(ShippingReply), command.SagaId.ToString()))
            {
                var replyRequest = new SendMessageRequest
                {
                    QueueUrl = repliesQueueUrl,
                    MessageBody = JsonSerializer.Serialize(cachedReply),
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>()
                };
                SqsTracePropagation.Inject(replyRequest.MessageAttributes);
                await _sqs.SendMessageAsync(replyRequest, ct);
            }

            return;
        }

        _logger.LogInformation(
            "Comando recebido: ScheduleShipping SagaId={SagaId}, OrderId={OrderId}, Address={ShippingAddress}",
            command.SagaId, command.OrderId, command.ShippingAddress);

        // Verificar simulacao de falha
        var shouldFail = message.MessageAttributes.TryGetValue("SimulateFailure", out var failAttr)
            && failAttr.StringValue.Equals("shipping", StringComparison.OrdinalIgnoreCase);

        await Task.Delay(200, ct);

        var reply = new ShippingReply
        {
            SagaId = command.SagaId,
            Success = !shouldFail,
            TrackingNumber = shouldFail ? null : $"TRACK-{Guid.NewGuid().ToString()[..8].ToUpperInvariant()}",
            ErrorMessage = shouldFail ? "Falha simulada no envio" : null
        };

        await _idempotencyStore.SaveAsync(command.IdempotencyKey, command.SagaId, reply);

        if (reply.Success)
            processActivity?.SetTag("shipping.tracking_number", reply.TrackingNumber);
        else
            processActivity?.SetStatus(ActivityStatusCode.Error, reply.ErrorMessage);

        using (SagaActivitySource.StartSendReply(nameof(ShippingReply), command.SagaId.ToString()))
        {
            var replyRequest = new SendMessageRequest
            {
                QueueUrl = repliesQueueUrl,
                MessageBody = JsonSerializer.Serialize(reply),
                MessageAttributes = new Dictionary<string, MessageAttributeValue>()
            };
            SqsTracePropagation.Inject(replyRequest.MessageAttributes);
            await _sqs.SendMessageAsync(replyRequest, ct);
        }

        _logger.LogInformation(
            "Reply enviado: ShippingReply SagaId={SagaId}, Success={Success}, TrackingNumber={TrackingNumber}, Error={Error}",
            reply.SagaId, reply.Success, reply.TrackingNumber, reply.ErrorMessage);
    }

    private async Task HandleCancelShippingAsync(Message message, string repliesQueueUrl, CancellationToken ct)
    {
        var command = JsonSerializer.Deserialize<CancelShipping>(message.Body)!;
        var parentContext = SqsTracePropagation.Extract(message.MessageAttributes);
        using var processActivity = SagaActivitySource.StartProcessCommand(
            nameof(CancelShipping), command.SagaId.ToString(), parentContext.ActivityContext);
        processActivity?.SetTag("saga.order_id", command.OrderId.ToString());
        processActivity?.SetTag("shipping.tracking_number", command.TrackingNumber);

        // Verificar idempotencia
        var cachedReply = await _idempotencyStore.TryGetAsync<CancelShippingReply>(command.IdempotencyKey);
        if (cachedReply is not null)
        {
            _logger.LogInformation(
                "Idempotency hit: CancelShipping SagaId={SagaId}, IdempotencyKey={IdempotencyKey}",
                command.SagaId, command.IdempotencyKey);

            using (SagaActivitySource.StartSendReply(nameof(CancelShippingReply), command.SagaId.ToString()))
            {
                var replyRequest = new SendMessageRequest
                {
                    QueueUrl = repliesQueueUrl,
                    MessageBody = JsonSerializer.Serialize(cachedReply),
                    MessageAttributes = new Dictionary<string, MessageAttributeValue>()
                };
                SqsTracePropagation.Inject(replyRequest.MessageAttributes);
                await _sqs.SendMessageAsync(replyRequest, ct);
            }

            return;
        }

        _logger.LogInformation(
            "Comando de compensacao: CancelShipping SagaId={SagaId}, OrderId={OrderId}, TrackingNumber={TrackingNumber}",
            command.SagaId, command.OrderId, command.TrackingNumber);

        await Task.Delay(200, ct);

        var reply = new CancelShippingReply
        {
            SagaId = command.SagaId,
            Success = true,
            TrackingNumber = command.TrackingNumber
        };

        await _idempotencyStore.SaveAsync(command.IdempotencyKey, command.SagaId, reply);

        using (SagaActivitySource.StartSendReply(nameof(CancelShippingReply), command.SagaId.ToString()))
        {
            var replyRequest = new SendMessageRequest
            {
                QueueUrl = repliesQueueUrl,
                MessageBody = JsonSerializer.Serialize(reply),
                MessageAttributes = new Dictionary<string, MessageAttributeValue>()
            };
            SqsTracePropagation.Inject(replyRequest.MessageAttributes);
            await _sqs.SendMessageAsync(replyRequest, ct);
        }

        _logger.LogInformation(
            "Reply de compensacao enviado: CancelShippingReply SagaId={SagaId}, TrackingNumber={TrackingNumber}",
            reply.SagaId, reply.TrackingNumber);
    }
}
