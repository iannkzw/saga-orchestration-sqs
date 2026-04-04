using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using OrderService.Data;
using OrderService.Models;
using Shared.Configuration;
using Shared.Contracts.Notifications;

namespace OrderService;

public class Worker(IAmazonSQS sqs, IServiceScopeFactory scopeFactory, ILogger<Worker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OrderService Worker iniciado — consumindo {Queue}", SqsConfig.OrderStatusUpdates);

        var queueUrl = (await sqs.GetQueueUrlAsync(SqsConfig.OrderStatusUpdates, stoppingToken)).QueueUrl;

        while (!stoppingToken.IsCancellationRequested)
        {
            var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                WaitTimeSeconds = 2,
                MaxNumberOfMessages = 10,
                MessageAttributeNames = ["All"]
            }, stoppingToken);

            if (response?.Messages is { Count: > 0 })
            {
                var tasks = response.Messages.Select(msg =>
                    ProcessMessageAsync(msg, queueUrl, stoppingToken));
                await Task.WhenAll(tasks);
            }

            await Task.Delay(200, stoppingToken);
        }
    }

    private async Task ProcessMessageAsync(Message msg, string queueUrl, CancellationToken ct)
    {
        SagaTerminatedNotification? notification;
        try
        {
            notification = JsonSerializer.Deserialize<SagaTerminatedNotification>(msg.Body,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Falha ao desserializar mensagem: {Body}", msg.Body);
            await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle, ct);
            return;
        }

        if (notification is null)
        {
            logger.LogWarning("Mensagem nula ou invalida: {Body}", msg.Body);
            await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle, ct);
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrderDbContext>();

        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == notification.OrderId, ct);
        if (order is null)
        {
            logger.LogWarning("Order nao encontrado para OrderId={OrderId} (SagaId={SagaId}) — descartando mensagem",
                notification.OrderId, notification.SagaId);
            await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle, ct);
            return;
        }

        var newStatus = notification.TerminalState == "Completed"
            ? OrderStatus.Completed
            : OrderStatus.Failed;

        order.Status = newStatus;
        order.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation(
            "Order {OrderId} atualizado para {Status} via SagaId={SagaId}",
            order.Id, newStatus, notification.SagaId);

        await sqs.DeleteMessageAsync(queueUrl, msg.ReceiptHandle, ct);
    }
}
