using System.Diagnostics;

namespace Shared.Telemetry;

public static class SagaActivitySource
{
    public const string Name = "SagaOrchestration";
    public static readonly ActivitySource Source = new(Name);

    public static Activity? StartSendCommand(string commandType, string sagaId)
    {
        var activity = Source.StartActivity($"send {commandType}", ActivityKind.Producer);
        activity?.SetTag("saga.id", sagaId);
        activity?.SetTag("saga.command_type", commandType);
        activity?.SetTag("messaging.system", "aws_sqs");
        activity?.SetTag("messaging.operation", "publish");
        return activity;
    }

    public static Activity? StartProcessCommand(string commandType, string sagaId, ActivityContext parentContext)
    {
        var activity = Source.StartActivity(
            $"process {commandType}",
            ActivityKind.Consumer,
            parentContext);
        activity?.SetTag("saga.id", sagaId);
        activity?.SetTag("saga.command_type", commandType);
        activity?.SetTag("messaging.system", "aws_sqs");
        activity?.SetTag("messaging.operation", "process");
        return activity;
    }

    public static Activity? StartSendReply(string replyType, string sagaId)
    {
        var activity = Source.StartActivity($"send {replyType}", ActivityKind.Producer);
        activity?.SetTag("saga.id", sagaId);
        activity?.SetTag("saga.reply_type", replyType);
        activity?.SetTag("messaging.system", "aws_sqs");
        activity?.SetTag("messaging.operation", "publish");
        return activity;
    }

    public static Activity? StartProcessReply(string replyType, string sagaId, string sagaState, ActivityContext parentContext)
    {
        var activity = Source.StartActivity(
            $"process {replyType}",
            ActivityKind.Consumer,
            parentContext);
        activity?.SetTag("saga.id", sagaId);
        activity?.SetTag("saga.reply_type", replyType);
        activity?.SetTag("saga.state", sagaState);
        activity?.SetTag("messaging.system", "aws_sqs");
        activity?.SetTag("messaging.operation", "process");
        return activity;
    }
}
