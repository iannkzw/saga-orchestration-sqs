using Microsoft.EntityFrameworkCore;

namespace OrderService.Diagnostics;

// Intercepta logs de qualquer categoria que carreguem DbUpdateConcurrencyException
// (tipicamente MassTransit.EntityFrameworkCoreIntegration.* quando xmin diverge) e
// incrementa o contador. Serve como sonda observacional sem acoplamento a API interna
// do repositorio de saga.
public sealed class ConcurrencyLoggerProvider(OptimisticRetryCounter counter) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new ConcurrencyLogger(counter);

    public void Dispose() { }

    private sealed class ConcurrencyLogger(OptimisticRetryCounter counter) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is null) return;
            if (ContainsConcurrencyException(exception))
                counter.Increment();
        }

        private static bool ContainsConcurrencyException(Exception? ex)
        {
            while (ex is not null)
            {
                if (ex is DbUpdateConcurrencyException) return true;
                ex = ex.InnerException;
            }
            return false;
        }
    }
}
