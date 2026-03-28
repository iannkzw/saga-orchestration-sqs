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

namespace SagaOrchestrator;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAmazonSQS _sqs;

    private readonly record struct QueueMapping(string QueueName, Type ReplyType);

    private static readonly QueueMapping[] _replyQueues =
    [
        new(SqsConfig.PaymentReplies, typeof(PaymentReply)),
        new(SqsConfig.InventoryReplies, typeof(InventoryReply)),
        new(SqsConfig.ShippingReplies, typeof(ShippingReply)),
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

        // Resolver URLs das filas uma vez
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
                    }, stoppingToken);

                    foreach (var message in receiveResponse.Messages)
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
        var reply = (BaseReply?)JsonSerializer.Deserialize(message.Body, mapping.ReplyType);
        if (reply is null)
        {
            _logger.LogWarning("Reply nulo ou invalido na fila {Queue}", mapping.QueueName);
            return;
        }

        _logger.LogInformation("Reply recebido: SagaId={SagaId}, Success={Success}, Queue={Queue}",
            reply.SagaId, reply.Success, mapping.QueueName);

        if (!reply.Success)
        {
            _logger.LogWarning("Reply com falha para SagaId={SagaId}: {Error} — compensacao sera tratada no M3",
                reply.SagaId, reply.ErrorMessage);
            await _sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SagaDbContext>();

        var saga = await db.Sagas.FirstOrDefaultAsync(s => s.Id == reply.SagaId, ct);
        if (saga is null)
        {
            _logger.LogWarning("Saga nao encontrada: {SagaId}", reply.SagaId);
            await _sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
            return;
        }

        var result = SagaStateMachine.TryAdvance(saga.CurrentState);
        if (result is null)
        {
            _logger.LogWarning("Transicao invalida para SagaId={SagaId}, estado atual={State}",
                saga.Id, saga.CurrentState);
            await _sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
            return;
        }

        saga.TransitionTo(result.NextState, mapping.ReplyType.Name);
        await db.SaveChangesAsync(ct);

        _logger.LogInformation("Saga {SagaId} transicionou para {State}", saga.Id, saga.CurrentState);

        // Enviar proximo comando se nao for estado terminal
        if (result.CommandQueue is not null)
        {
            await SendNextCommandAsync(saga, result.CommandQueue, ct);
        }
        else
        {
            _logger.LogInformation("Saga {SagaId} completada com sucesso", saga.Id);
        }

        await _sqs.DeleteMessageAsync(queueUrl, message.ReceiptHandle, ct);
    }

    private async Task SendNextCommandAsync(SagaInstance saga, string commandQueue, CancellationToken ct)
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

        var queueUrlResponse = await _sqs.GetQueueUrlAsync(commandQueue, ct);
        await _sqs.SendMessageAsync(new SendMessageRequest
        {
            QueueUrl = queueUrlResponse.QueueUrl,
            MessageBody = JsonSerializer.Serialize(command, command.GetType())
        }, ct);

        _logger.LogInformation("Comando enviado para {Queue}: SagaId={SagaId}", commandQueue, saga.Id);
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
