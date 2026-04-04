namespace Shared.Contracts.Notifications;

public record SagaTerminatedNotification(
    Guid SagaId,
    Guid OrderId,
    string TerminalState
);
