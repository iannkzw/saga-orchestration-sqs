using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.EntityFrameworkCore;
using SagaOrchestrator.Data;
using SagaOrchestrator.Models;
using SagaOrchestrator.StateMachine;
using Shared.Configuration;
using Shared.Contracts.Commands;
using Shared.Extensions;
using Shared.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHostedService<SagaOrchestrator.Worker>();

var sqsServiceUrl = builder.Configuration["AWS_SERVICE_URL"] ?? "http://localhost:4566";
builder.Services.AddSagaConnectivity(sqsServiceUrl);

var connectionString = builder.Configuration.GetConnectionString("SagaDb")
    ?? "Host=localhost;Port=5432;Database=saga_db;Username=saga;Password=saga_pass";
builder.Services.AddDbContext<SagaDbContext>(options =>
    options.UseNpgsql(connectionString));

var app = builder.Build();

// EnsureCreated no startup (PoC — sem migrations formais)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<SagaDbContext>();
    db.Database.EnsureCreated();
}

app.MapGet("/health", (StartupConnectivityCheck checks) => Results.Ok(new
{
    status = "healthy",
    service = "SagaOrchestrator",
    connections = new
    {
        sqs = checks.SqsHealthy,
        postgres = checks.PostgresHealthy
    }
}));

app.MapPost("/sagas", async (HttpContext context, SagaDbContext db, IAmazonSQS sqs) =>
{
    var command = await context.Request.ReadFromJsonAsync<CreateOrder>();
    if (command is null)
        return Results.BadRequest("Comando invalido");

    // Capturar header de simulacao de falha
    var simulateFailure = context.Request.Headers["X-Simulate-Failure"].FirstOrDefault();

    var saga = new SagaInstance
    {
        Id = command.SagaId != Guid.Empty ? command.SagaId : Guid.NewGuid(),
        OrderId = command.OrderId,
        TotalAmount = command.TotalAmount,
        ItemsJson = JsonSerializer.Serialize(command.Items),
        CurrentState = SagaState.Pending,
        SimulateFailure = simulateFailure,
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow
    };

    db.Sagas.Add(saga);

    // Transicionar para PaymentProcessing e enviar comando
    var result = SagaStateMachine.TryAdvance(saga.CurrentState);
    if (result is null)
        return Results.Problem("Estado invalido para iniciar saga");

    saga.TransitionTo(result.NextState, "SagaCreated");
    await db.SaveChangesAsync();

    // Enviar ProcessPayment para fila
    var paymentCommand = new ProcessPayment
    {
        SagaId = saga.Id,
        OrderId = saga.OrderId,
        Amount = saga.TotalAmount,
        IdempotencyKey = $"{saga.Id}-payment",
        Timestamp = DateTime.UtcNow
    };

    var queueUrlResponse = await sqs.GetQueueUrlAsync(SqsConfig.PaymentCommands);
    var sendRequest = new SendMessageRequest
    {
        QueueUrl = queueUrlResponse.QueueUrl,
        MessageBody = JsonSerializer.Serialize(paymentCommand),
        MessageAttributes = new Dictionary<string, MessageAttributeValue>
        {
            ["CommandType"] = new()
            {
                DataType = "String",
                StringValue = nameof(ProcessPayment)
            }
        }
    };

    if (!string.IsNullOrEmpty(simulateFailure))
    {
        sendRequest.MessageAttributes["SimulateFailure"] = new MessageAttributeValue
        {
            DataType = "String",
            StringValue = simulateFailure
        };
    }

    await sqs.SendMessageAsync(sendRequest);

    return Results.Created($"/sagas/{saga.Id}", new
    {
        sagaId = saga.Id,
        orderId = saga.OrderId,
        state = saga.CurrentState.ToString()
    });
});

app.MapGet("/sagas/{id:guid}", async (Guid id, SagaDbContext db) =>
{
    var saga = await db.Sagas
        .Include(s => s.Transitions.OrderBy(t => t.Timestamp))
        .FirstOrDefaultAsync(s => s.Id == id);

    if (saga is null)
        return Results.NotFound();

    return Results.Ok(new
    {
        sagaId = saga.Id,
        orderId = saga.OrderId,
        state = saga.CurrentState.ToString(),
        createdAt = saga.CreatedAt,
        updatedAt = saga.UpdatedAt,
        transitions = saga.Transitions.Select(t => new
        {
            from = t.FromState,
            to = t.ToState,
            triggeredBy = t.TriggeredBy,
            timestamp = t.Timestamp
        })
    });
});

// === DLQ Visibility ===

app.MapGet("/dlq", async (IAmazonSQS sqs) =>
{
    var result = new Dictionary<string, object>();

    foreach (var dlqName in SqsConfig.AllDlqNames)
    {
        try
        {
            var queueUrl = (await sqs.GetQueueUrlAsync(dlqName)).QueueUrl;
            var response = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = 10,
                VisibilityTimeout = 0, // peek — nao esconde a mensagem
                MessageSystemAttributeNames = ["All"]
            });

            var messages = response.Messages.Select(m =>
            {
                // Tentar parsear body como JSON
                object body;
                try { body = JsonSerializer.Deserialize<JsonElement>(m.Body); }
                catch { body = m.Body; }

                return new
                {
                    messageId = m.MessageId,
                    receiptHandle = m.ReceiptHandle,
                    body,
                    approximateReceiveCount = m.Attributes.GetValueOrDefault("ApproximateReceiveCount"),
                    sentTimestamp = m.Attributes.GetValueOrDefault("SentTimestamp")
                };
            });

            result[dlqName] = new { count = messages.Count(), messages };
        }
        catch (Exception ex)
        {
            result[dlqName] = new { count = 0, messages = Array.Empty<object>(), error = ex.Message };
        }
    }

    return Results.Ok(result);
});

app.MapPost("/dlq/redrive", async (HttpContext context, IAmazonSQS sqs) =>
{
    var request = await context.Request.ReadFromJsonAsync<JsonElement>();

    var queueName = request.GetProperty("queueName").GetString();
    var receiptHandle = request.GetProperty("receiptHandle").GetString();

    if (string.IsNullOrEmpty(queueName) || string.IsNullOrEmpty(receiptHandle))
        return Results.BadRequest("queueName e receiptHandle sao obrigatorios");

    // Validar que e uma DLQ conhecida
    if (!SqsConfig.DlqToOriginalQueue.TryGetValue(queueName, out var originalQueue))
        return Results.BadRequest($"DLQ desconhecida: {queueName}");

    var dlqUrl = (await sqs.GetQueueUrlAsync(queueName)).QueueUrl;
    var originalUrl = (await sqs.GetQueueUrlAsync(originalQueue)).QueueUrl;

    // Ler mensagem da DLQ pelo receipt handle (receber para obter o body)
    var dlqMessages = await sqs.ReceiveMessageAsync(new ReceiveMessageRequest
    {
        QueueUrl = dlqUrl,
        MaxNumberOfMessages = 1,
        VisibilityTimeout = 30
    });

    var message = dlqMessages.Messages.FirstOrDefault(m => m.ReceiptHandle == receiptHandle);
    if (message is null)
    {
        // Tentar enviar direto — receipt handle pode ter mudado, buscar qualquer mensagem
        return Results.NotFound("Mensagem nao encontrada na DLQ. O receiptHandle pode ter expirado.");
    }

    // Reenviar para fila original
    await sqs.SendMessageAsync(new SendMessageRequest
    {
        QueueUrl = originalUrl,
        MessageBody = message.Body
    });

    // Deletar da DLQ
    await sqs.DeleteMessageAsync(dlqUrl, receiptHandle);

    return Results.Ok(new
    {
        redriven = true,
        fromDlq = queueName,
        toQueue = originalQueue,
        messageId = message.MessageId
    });
});

app.Run();
