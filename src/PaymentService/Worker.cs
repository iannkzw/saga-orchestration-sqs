using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Shared.Configuration;
using Shared.Contracts.Commands;
using Shared.Contracts.Replies;

namespace PaymentService;

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
        _logger.LogInformation("PaymentService worker started");

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
                    MaxNumberOfMessages = 10
                }, stoppingToken);

                foreach (var message in response.Messages)
                {
                    try
                    {
                        var command = JsonSerializer.Deserialize<ProcessPayment>(message.Body)!;

                        _logger.LogInformation(
                            "Comando recebido: ProcessPayment SagaId={SagaId}, OrderId={OrderId}, Amount={Amount}",
                            command.SagaId, command.OrderId, command.Amount);

                        await Task.Delay(200, stoppingToken);

                        var reply = new PaymentReply
                        {
                            SagaId = command.SagaId,
                            Success = true,
                            TransactionId = Guid.NewGuid().ToString()
                        };

                        await _sqs.SendMessageAsync(new SendMessageRequest
                        {
                            QueueUrl = replyQueueUrl,
                            MessageBody = JsonSerializer.Serialize(reply)
                        }, stoppingToken);

                        await _sqs.DeleteMessageAsync(commandQueueUrl, message.ReceiptHandle, stoppingToken);

                        _logger.LogInformation(
                            "Reply enviado: PaymentReply SagaId={SagaId}, TransactionId={TransactionId}",
                            reply.SagaId, reply.TransactionId);
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
}
