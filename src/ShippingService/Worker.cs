using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Shared.Configuration;
using Shared.Contracts.Commands;
using Shared.Contracts.Replies;

namespace ShippingService;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IAmazonSQS _sqs;

    public Worker(ILogger<Worker> logger, IAmazonSQS sqs)
    {
        _logger = logger;
        _sqs = sqs;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ShippingService worker started");

        var commandsQueueUrl = (await _sqs.GetQueueUrlAsync(SqsConfig.ShippingCommands, stoppingToken)).QueueUrl;
        var repliesQueueUrl = (await _sqs.GetQueueUrlAsync(SqsConfig.ShippingReplies, stoppingToken)).QueueUrl;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var response = await _sqs.ReceiveMessageAsync(new ReceiveMessageRequest
                {
                    QueueUrl = commandsQueueUrl,
                    WaitTimeSeconds = 2,
                    MaxNumberOfMessages = 10
                }, stoppingToken);

                foreach (var message in response.Messages)
                {
                    try
                    {
                        var command = JsonSerializer.Deserialize<ScheduleShipping>(message.Body)!;

                        _logger.LogInformation(
                            "Comando recebido: ScheduleShipping SagaId={SagaId}, OrderId={OrderId}, Address={ShippingAddress}",
                            command.SagaId, command.OrderId, command.ShippingAddress);

                        await Task.Delay(200, stoppingToken);

                        var reply = new ShippingReply
                        {
                            SagaId = command.SagaId,
                            Success = true,
                            TrackingNumber = $"TRACK-{Guid.NewGuid().ToString()[..8].ToUpperInvariant()}"
                        };

                        await _sqs.SendMessageAsync(new SendMessageRequest
                        {
                            QueueUrl = repliesQueueUrl,
                            MessageBody = JsonSerializer.Serialize(reply)
                        }, stoppingToken);

                        await _sqs.DeleteMessageAsync(commandsQueueUrl, message.ReceiptHandle, stoppingToken);

                        _logger.LogInformation(
                            "Reply enviado: ShippingReply SagaId={SagaId}, TrackingNumber={TrackingNumber}",
                            reply.SagaId, reply.TrackingNumber);
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
}
