using Microsoft.Extensions.Logging;

namespace AtlasMail.Protocols;

/// <summary>Logger no-op compartido por los componentes de la librería Protocols.</summary>
internal sealed class NullLogger<T> : ILogger<T> where T : class
{
    public static readonly NullLogger<T> Instance = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => default;
    public bool IsEnabled(LogLevel logLevel) => false;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
}