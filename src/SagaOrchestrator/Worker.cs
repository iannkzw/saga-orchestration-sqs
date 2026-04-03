using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using SagaOrchestrator.Data;
using SagaOrchestrator.Models;
using SagaOrchestrator.StateMachine;
using Shared.Configuration;
using Shared.Contracts.Commands;
using Shared.Contracts.Replies;
using Shared.Telemetry;

namespace SagaOrchestrator;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAmazonSQS _sqs;

    private readonly record struct QueueMapping(string QueueName, string ReplyTypeName);

    private static readonly QueueMapping[] _replyQueues =
    [
        new(SqsConfig.PaymentReplies, "PaymentReplies"),
        new(SqsConfig.InventoryReplies, "InventoryReplies"),
        new(SqsConfig.ShippingReplies, "ShippingReplies"),
    ];

    public Worker(ILogger<Worker> logger, IServiceScopeFactory scopeFactory, IAmazonSQS sqs)
    {
        _logger = logger;
        _scopeFactory = scopeFactory;
        _sqs = sqs;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SagaOrchestrator worker started — polling reply queues");

        var queueUrls = new Dictionary<string, string>();
        foreach (var mapping in _replyQueues)
        {
            var response = await _sqs.GetQueueUrlAsync(mapping.QueueName, stoppingToken);
            queueUrls[mapping.QueueName] = response.QueueUrl;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var mapping in _replyQueues)
            {
                try
                {
                    var receiveResponse = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                    {
                        QueueUrl = queueUrls[mapping.QueueName],
                        MaxNumberOfMessages = 10,
                        WaitTimeSeconds = 1,
                        MessageAttributeNames = ["All"]
                    }, stoppingToken);

                    foreach (var message in receiveResponse?.Messages ?? [])
                    {
                        await ProcessReplyAsync(message, mapping, queueUrls[mapping.QueueName], stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Erro ao processar fila {Queue}", mapping.QueueName);
                }
            }

            await Task.Delay(500, stoppingToken);
        }
    }

    private async Task ProcessReplyAsync(Message message, QueueMapping mapping, string queueUrl, CancellationToken ct)
    {
        // Deserializar campos base para obter SagaId e Success
        var baseReply = JsonSerializer.Deserialize<JsonElement>(message.Body);
        var sagaId = baseReply.GetProperty("SagaId").GetGuid();
        var success = baseReply.GetProperty("Success").GetBoolean();

        var parentContext = SqsTracePropagation.Extract(message.MessageAttributes);
        using var activity = SagaActivitySource.StartProcessReply(
            mapping.ReplyTypeName, sagaId.ToString(), "Processing", parentContext.ActivityContext);

        _logger.LogInformation("Reply recebido: SagaId={SagaId}, Success={Success}, Queue={Queue}",
            sagaId, success, mapping.QueueName);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SagaDbContext>();

        var saga = await db.Sagas.FirstOrDefaultAsync(s => s.Id == sagaId, ct);
        if (saga is null)
        {
            _logger.LogWarning("Saga nao encontrada: {SagaId}", sagaId);
            await _sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
            return;
        }

        if (SagaStateMachine.IsCompensating(saga.CurrentState))
        {
            await HandleCompensationReplyAsync(saga, success, mapping, db, ct);
        }
        else if (!success)
        {
            await HandleFailureAsync(saga, baseReply, mapping, db, ct);
        }
        else
        {
            await HandleSuccessAsync(saga, baseReply, mapping, db, ct);
        }

        await _sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
    }

    private async Task HandleSuccessAsync(SagaInstance saga, JsonElement replyJson, QueueMapping mapping, SagaDbContext db, CancellationToken ct)
    {
        // Armazenar dados de compensacao do step que completou
        StoreCompensationData(saga, replyJson);

        var result = SagaStateMachine.TryAdvance(saga.CurrentState);
        if (result is null)
        {
            _logger.LogWarning("Transicao invalida para SagaId={SagaId}, estado={State}", saga.Id, saga.CurrentState);
            return;
        }

        var transition = saga.TransitionTo(result.NextState, mapping.ReplyTypeName);
        db.SagaStateTransitions.Add(transition);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Saga {SagaId} transicionou para {State}", saga.Id, saga.CurrentState);

        if (result.CommandQueue is not null)
        {
            await SendForwardCommandAsync(saga, result.CommandQueue, ct);
        }
        else
        {
            _logger.LogInformation("Saga {SagaId} completada com sucesso", saga.Id);
        }
    }

    private async Task HandleFailureAsync(SagaInstance saga, JsonElement replyJson, QueueMapping mapping, SagaDbContext db, CancellationToken ct)
    {
        var errorMessage = replyJson.TryGetProperty("ErrorMessage", out var em) ? em.GetString() : null;
        _logger.LogWarning("Falha na saga {SagaId} no estado {State}: {Error}. Iniciando compensacao.",
            saga.Id, saga.CurrentState, errorMessage);

        var result = SagaStateMachine.TryCompensate(saga.CurrentState);
        if (result is null)
        {
            _logger.LogWarning("Sem compensacao definida para estado {State}", saga.CurrentState);
            db.SagaStateTransitions.Add(saga.TransitionTo(SagaState.Failed, $"{mapping.ReplyTypeName}:Failure"));
            await db.SaveChangesAsync(ct);
            return;
        }

        var transition = saga.TransitionTo(result.NextState, $"{mapping.ReplyTypeName}:Failure");
        db.SagaStateTransitions.Add(transition);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Saga {SagaId} iniciou compensacao -> {State}", saga.Id, saga.CurrentState);

        if (result.CommandQueue is not null)
        {
            await SendCompensationCommandAsync(saga, result.CommandQueue, ct);
        }
        else
        {
            _logger.LogInformation("Saga {SagaId} falhou sem necessidade de compensacao", saga.Id);
        }
    }

    private async Task HandleCompensationReplyAsync(SagaInstance saga, bool success, QueueMapping mapping, SagaDbContext db, CancellationToken ct)
    {
        if (!success)
        {
            _logger.LogError("Falha na compensacao da saga {SagaId} no estado {State}. Intervencao manual necessaria.",
                saga.Id, saga.CurrentState);
            db.SagaStateTransitions.Add(saga.TransitionTo(SagaState.Failed, $"{mapping.ReplyTypeName}:CompensationFailure"));
            await db.SaveChangesAsync(ct);
            return;
        }

        var result = SagaStateMachine.TryAdvanceCompensation(saga.CurrentState);
        if (result is null)
        {
            _logger.LogWarning("Transicao de compensacao invalida para estado {State}", saga.CurrentState);
            return;
        }

        db.SagaStateTransitions.Add(saga.TransitionTo(result.NextState, $"{mapping.ReplyTypeName}:Compensated"));
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Saga {SagaId} compensacao avancou para {State}", saga.Id, saga.CurrentState);

        if (result.CommandQueue is not null)
        {
            await SendCompensationCommandAsync(saga, result.CommandQueue, ct);
        }
        else
        {
            _logger.LogInformation("Saga {SagaId} compensacao completa — estado final: Failed", saga.Id);
        }
    }

    private void StoreCompensationData(SagaInstance saga, JsonElement replyJson)
    {
        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(saga.CompensationDataJson)
            ?? new Dictionary<string, string>();

        switch (saga.CurrentState)
        {
            case SagaState.PaymentProcessing when replyJson.TryGetProperty("TransactionId", out var tid):
                data["TransactionId"] = tid.GetString() ?? string.Empty;
                break;
            case SagaState.InventoryReserving when replyJson.TryGetProperty("ReservationId", out var rid):
                data["ReservationId"] = rid.GetString() ?? string.Empty;
                break;
            case SagaState.ShippingScheduling when replyJson.TryGetProperty("TrackingNumber", out var tn):
                data["TrackingNumber"] = tn.GetString() ?? string.Empty;
                break;
        }

        saga.CompensationDataJson = JsonSerializer.Serialize(data);
    }

    private Dictionary<string, string> GetCompensationData(SagaInstance saga)
    {
        return JsonSerializer.Deserialize<Dictionary<string, string>>(saga.CompensationDataJson)
            ?? new Dictionary<string, string>();
    }

    private async Task SendForwardCommandAsync(SagaInstance saga, string commandQueue, CancellationToken ct)
    {
        object command = saga.CurrentState switch
        {
            SagaState.InventoryReserving => new ReserveInventory
            {
                SagaId = saga.Id,
                OrderId = saga.OrderId,
                Items = DeserializeItems(saga.ItemsJson),
                IdempotencyKey = $"{saga.Id}-inventory",
                Timestamp = DateTime.UtcNow
            },
            SagaState.ShippingScheduling => new ScheduleShipping
            {
                SagaId = saga.Id,
                OrderId = saga.OrderId,
                ShippingAddress = "Endereco padrao (PoC)",
                IdempotencyKey = $"{saga.Id}-shipping",
                Timestamp = DateTime.UtcNow
            },
            _ => throw new InvalidOperationException($"Comando nao mapeado para estado {saga.CurrentState}")
        };

        await SendCommandToQueueAsync(command, commandQueue, saga.SimulateFailure, ct);
    }

    private async Task SendCompensationCommandAsync(SagaInstance saga, string commandQueue, CancellationToken ct)
    {
        var compData = GetCompensationData(saga);

        object command = saga.CurrentState switch
        {
            SagaState.PaymentRefunding => new RefundPayment
            {
                SagaId = saga.Id,
                OrderId = saga.OrderId,
                Amount = saga.TotalAmount,
                TransactionId = compData.GetValueOrDefault("TransactionId", string.Empty),
                IdempotencyKey = $"{saga.Id}-refund-payment",
                Timestamp = DateTime.UtcNow
            },
            SagaState.InventoryReleasing => new ReleaseInventory
            {
                SagaId = saga.Id,
                OrderId = saga.OrderId,
                ReservationId = compData.GetValueOrDefault("ReservationId", string.Empty),
                IdempotencyKey = $"{saga.Id}-release-inventory",
                Timestamp = DateTime.UtcNow
            },
            SagaState.ShippingCancelling => new CancelShipping
            {
                SagaId = saga.Id,
                OrderId = saga.OrderId,
                TrackingNumber = compData.GetValueOrDefault("TrackingNumber", string.Empty),
                IdempotencyKey = $"{saga.Id}-cancel-shipping",
                Timestamp = DateTime.UtcNow
            },
            _ => throw new InvalidOperationException($"Comando de compensacao nao mapeado para estado {saga.CurrentState}")
        };

        await SendCommandToQueueAsync(command, commandQueue, null, ct);
    }

    private async Task SendCommandToQueueAsync(object command, string commandQueue, string? simulateFailure, CancellationToken ct)
    {
        var queueUrlResponse = await _sqs.GetQueueUrlAsync(commandQueue, ct);
        var baseCommand = (BaseCommand)command;

        var request = new SendMessageRequest
        {
            QueueUrl = queueUrlResponse.QueueUrl,
            MessageBody = JsonSerializer.Serialize(command, command.GetType()),
            MessageAttributes = new Dictionary<string, MessageAttributeValue>
            {
                ["CommandType"] = new()
                {
                    DataType = "String",
                    StringValue = command.GetType().Name
                }
            }
        };

        if (!string.IsNullOrEmpty(simulateFailure))
        {
            request.MessageAttributes["SimulateFailure"] = new MessageAttributeValue
            {
                DataType = "String",
                StringValue = simulateFailure
            };
        }

        using (SagaActivitySource.StartSendCommand(command.GetType().Name, baseCommand.SagaId.ToString()))
        {
            SqsTracePropagation.Inject(request.MessageAttributes);
            await _sqs.SendMessageAsync(request, ct);
        }

        _logger.LogInformation("Comando {CommandType} enviado para {Queue}: SagaId={SagaId}",
            command.GetType().Name, commandQueue, baseCommand.SagaId);
    }

    private static List<InventoryItem> DeserializeItems(string itemsJson)
    {
        try
        {
            var orderItems = JsonSerializer.Deserialize<List<OrderItem>>(itemsJson) ?? [];
            return orderItems.Select(i => new InventoryItem
            {
                ProductId = i.ProductId,
                Quantity = i.Quantity
            }).ToList();
        }
        catch
        {
            return [];
        }
    }
}
