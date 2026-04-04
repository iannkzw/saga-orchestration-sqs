using System.Diagnostics;
using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using SagaOrchestrator.Data;
using SagaOrchestrator.Models;
using SagaOrchestrator.StateMachine;
using Shared.Configuration;
using Shared.Contracts.Commands;
using Shared.Contracts.Notifications;
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

    private static readonly string[] _commandQueueNames =
    [
        SqsConfig.PaymentCommands,
        SqsConfig.InventoryCommands,
        SqsConfig.ShippingCommands,
    ];

    private readonly Dictionary<string, string> _commandQueueUrls = new();
    private string _orderStatusQueueUrl = string.Empty;

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

        foreach (var queueName in _commandQueueNames)
        {
            var r = await _sqs.GetQueueUrlAsync(queueName, stoppingToken);
            _commandQueueUrls[queueName] = r.QueueUrl;
        }

        var statusQueueResponse = await _sqs.GetQueueUrlAsync(SqsConfig.OrderStatusUpdates, stoppingToken);
        _orderStatusQueueUrl = statusQueueResponse.QueueUrl;

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

        // TODO [Dívida Técnica]: Validar se mapping.ReplyTypeName é esperado para saga.CurrentState.
        // Em re-entrega cruzada por timeout de visibilidade, um reply de estado anterior poderia
        // avançar a saga incorretamente. Aceitável para PoC; mitigar em produção.
        if (!baseReply.TryGetProperty("SagaId", out var sagaIdProp) ||
            !baseReply.TryGetProperty("Success", out var successProp))
        {
            _logger.LogError("Mensagem malformada em {Queue}: {Body}", mapping.QueueName, message.Body);
            await _sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
            return;
        }
        var sagaId = sagaIdProp.GetGuid();
        var success = successProp.GetBoolean();

        var parentContext = SqsTracePropagation.Extract(message.MessageAttributes);
        using var activity = SagaActivitySource.StartProcessReply(
            mapping.ReplyTypeName, sagaId.ToString(), "Processing", parentContext.ActivityContext);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SagaDbContext>();

        var saga = await db.Sagas.FirstOrDefaultAsync(s => s.Id == sagaId, ct);
        if (saga is null)
        {
            _logger.LogWarning("Saga nao encontrada: {SagaId}", sagaId);
            await _sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
            return;
        }

        var fromState = saga.CurrentState;
        activity?.SetTag("saga.order_id", saga.OrderId.ToString());
        activity?.SetTag("saga.from_state", fromState.ToString());
        activity?.SetTag("saga.reply_success", success);

        _logger.LogInformation(
            "Reply recebido: SagaId={SagaId}, OrderId={OrderId}, State={State}, Success={Success}, Queue={Queue}",
            sagaId, saga.OrderId, saga.CurrentState, success, mapping.QueueName);

        if (SagaStateMachine.IsCompensating(saga.CurrentState))
        {
            await HandleCompensationReplyAsync(saga, success, mapping, db, ct);
        }
        else if (!success)
        {
            await HandleFailureAsync(saga, baseReply, mapping, db, ct);
            activity?.SetStatus(ActivityStatusCode.Error, "Saga step failed — compensation triggered");
        }
        else
        {
            await HandleSuccessAsync(saga, baseReply, mapping, db, ct);
        }

        activity?.SetTag("saga.to_state", saga.CurrentState.ToString());

        await _sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
    }

    private async Task HandleSuccessAsync(SagaInstance saga, JsonElement replyJson, QueueMapping mapping, SagaDbContext db, CancellationToken ct)
    {
        // Armazenar dados de compensacao do step que completou
        StoreCompensationData(saga, replyJson);

        var fromState = saga.CurrentState;
        var result = SagaStateMachine.TryAdvance(saga.CurrentState);
        if (result is null)
        {
            _logger.LogWarning("Transicao invalida para SagaId={SagaId}, OrderId={OrderId}, estado={State}",
                saga.Id, saga.OrderId, saga.CurrentState);
            return;
        }

        var transition = saga.TransitionTo(result.NextState, mapping.ReplyTypeName);
        db.SagaStateTransitions.Add(transition);

        if (result.CommandQueue is not null)
        {
            _logger.LogInformation(
                "Saga {SagaId} OrderId={OrderId}: {FromState} → {ToState}, enviando proximo comando",
                saga.Id, saga.OrderId, fromState, saga.CurrentState);
            // Enviar comando SQS antes do SaveChanges: idempotency no consumidor absorve reenvio
            await SendForwardCommandAsync(saga, result.CommandQueue, ct);
        }
        else
        {
            _logger.LogInformation(
                "Saga {SagaId} OrderId={OrderId}: {FromState} → {ToState} — CONCLUIDA COM SUCESSO",
                saga.Id, saga.OrderId, fromState, saga.CurrentState);
        }

        // TODO [Transactional Outbox]: salvar comando na mesma tx do DB e publicar via job separado
        //      para garantir entrega exactly-once sem dual-write.
        await db.SaveChangesAsync(ct);

        if (SagaStateMachine.IsTerminal(saga.CurrentState))
            await PublishSagaTerminatedAsync(saga, ct);
    }

    private async Task HandleFailureAsync(SagaInstance saga, JsonElement replyJson, QueueMapping mapping, SagaDbContext db, CancellationToken ct)
    {
        var errorMessage = replyJson.TryGetProperty("ErrorMessage", out var em) ? em.GetString() : null;
        var failedState = saga.CurrentState;

        _logger.LogWarning(
            "FALHA na saga {SagaId} OrderId={OrderId} no estado {State}: {Error}. Iniciando compensacao.",
            saga.Id, saga.OrderId, saga.CurrentState, errorMessage);

        var result = SagaStateMachine.TryCompensate(saga.CurrentState);
        if (result is null)
        {
            _logger.LogWarning("Sem compensacao definida para estado {State} — saga {SagaId} marcada como Failed",
                saga.CurrentState, saga.Id);
            db.SagaStateTransitions.Add(saga.TransitionTo(SagaState.Failed, $"{mapping.ReplyTypeName}:Failure"));
            await db.SaveChangesAsync(ct);
            await PublishSagaTerminatedAsync(saga, ct);
            return;
        }

        var transition = saga.TransitionTo(result.NextState, $"{mapping.ReplyTypeName}:Failure");
        db.SagaStateTransitions.Add(transition);

        if (result.CommandQueue is not null)
        {
            _logger.LogInformation(
                "Saga {SagaId} OrderId={OrderId}: {FailedState} → {CompensationState}, enviando comando de compensacao",
                saga.Id, saga.OrderId, failedState, saga.CurrentState);
            // Enviar comando SQS antes do SaveChanges: idempotency no consumidor absorve reenvio
            await SendCompensationCommandAsync(saga, result.CommandQueue, ct);
        }
        else
        {
            _logger.LogInformation(
                "Saga {SagaId} OrderId={OrderId}: {FailedState} → Failed (sem compensacao necessaria)",
                saga.Id, saga.OrderId, failedState);
        }

        // TODO [Transactional Outbox]: salvar comando na mesma tx do DB e publicar via job separado
        //      para garantir entrega exactly-once sem dual-write.
        await db.SaveChangesAsync(ct);

        if (SagaStateMachine.IsTerminal(saga.CurrentState))
            await PublishSagaTerminatedAsync(saga, ct);
    }

    private async Task HandleCompensationReplyAsync(SagaInstance saga, bool success, QueueMapping mapping, SagaDbContext db, CancellationToken ct)
    {
        if (!success)
        {
            _logger.LogError(
                "Falha na compensacao da saga {SagaId} OrderId={OrderId} no estado {State}. Intervencao manual necessaria.",
                saga.Id, saga.OrderId, saga.CurrentState);
            db.SagaStateTransitions.Add(saga.TransitionTo(SagaState.Failed, $"{mapping.ReplyTypeName}:CompensationFailure"));
            await db.SaveChangesAsync(ct);
            await PublishSagaTerminatedAsync(saga, ct);
            return;
        }

        var fromState = saga.CurrentState;
        var result = SagaStateMachine.TryAdvanceCompensation(saga.CurrentState);
        if (result is null)
        {
            _logger.LogWarning("Transicao de compensacao invalida para estado {State} na saga {SagaId}",
                saga.CurrentState, saga.Id);
            return;
        }

        db.SagaStateTransitions.Add(saga.TransitionTo(result.NextState, $"{mapping.ReplyTypeName}:Compensated"));
        await db.SaveChangesAsync(ct);

        if (result.CommandQueue is not null)
        {
            _logger.LogInformation(
                "Saga {SagaId} OrderId={OrderId}: compensacao {FromState} → {ToState}, proximo passo",
                saga.Id, saga.OrderId, fromState, saga.CurrentState);
            await SendCompensationCommandAsync(saga, result.CommandQueue, ct);
        }
        else
        {
            _logger.LogInformation(
                "Saga {SagaId} OrderId={OrderId}: compensacao {FromState} → Failed — COMPENSACAO COMPLETA",
                saga.Id, saga.OrderId, fromState);
            await PublishSagaTerminatedAsync(saga, ct);
        }
    }

    private async Task PublishSagaTerminatedAsync(SagaInstance saga, CancellationToken ct)
    {
        var notification = new SagaTerminatedNotification(saga.Id, saga.OrderId, saga.CurrentState.ToString());
        var request = new SendMessageRequest
        {
            QueueUrl = _orderStatusQueueUrl,
            MessageBody = JsonSerializer.Serialize(notification)
        };
        await _sqs.SendMessageAsync(request, ct);
        _logger.LogInformation(
            "SagaTerminated publicado: SagaId={SagaId}, OrderId={OrderId}, State={State}",
            saga.Id, saga.OrderId, saga.CurrentState);
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
        var queueUrl = _commandQueueUrls[commandQueue];
        var baseCommand = (BaseCommand)command;

        var request = new SendMessageRequest
        {
            QueueUrl = queueUrl,
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

        using var sendActivity = SagaActivitySource.StartSendCommand(command.GetType().Name, baseCommand.SagaId.ToString());
        // OrderId está disponível em todos os comandos concretos via propriedade direta
        if (command is ProcessPayment pp) sendActivity?.SetTag("saga.order_id", pp.OrderId.ToString());
        else if (command is ReserveInventory ri) sendActivity?.SetTag("saga.order_id", ri.OrderId.ToString());
        else if (command is ScheduleShipping ss) sendActivity?.SetTag("saga.order_id", ss.OrderId.ToString());
        else if (command is RefundPayment rp) { sendActivity?.SetTag("saga.order_id", rp.OrderId.ToString()); sendActivity?.SetTag("payment.amount", rp.Amount.ToString()); }
        else if (command is ReleaseInventory rl) { sendActivity?.SetTag("saga.order_id", rl.OrderId.ToString()); sendActivity?.SetTag("inventory.reservation_id", rl.ReservationId); }
        else if (command is CancelShipping cs) { sendActivity?.SetTag("saga.order_id", cs.OrderId.ToString()); sendActivity?.SetTag("shipping.tracking_number", cs.TrackingNumber); }
        SqsTracePropagation.Inject(request.MessageAttributes);
        await _sqs.SendMessageAsync(request, ct);

        _logger.LogInformation("Comando {CommandType} enviado: SagaId={SagaId}, Queue={Queue}",
            command.GetType().Name, baseCommand.SagaId, commandQueue);
    }

    private List<InventoryItem> DeserializeItems(string itemsJson)
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao desserializar ItemsJson da saga. Json={ItemsJson}", itemsJson);
            return [];
        }
    }
}
