using Avalonia.Logging;

namespace TypeWhisper.UiTests;

internal sealed class BindingErrorSink(ILogSink? inner) : ILogSink
{
    public ILogSink? Inner { get; } = inner;

    public List<string> Errors { get; } = [];

    public bool IsEnabled(LogEventLevel level, string area) =>
        IsBindingError(level, area) || Inner?.IsEnabled(level, area) == true;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        if (IsBindingError(level, area))
            Errors.Add($"{source}: {messageTemplate}");
        Inner?.Log(level, area, source, messageTemplate);
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        if (IsBindingError(level, area))
            Errors.Add($"{source}: {messageTemplate} [{string.Join(", ", propertyValues)}]");
        Inner?.Log(level, area, source, messageTemplate, propertyValues);
    }

    private static bool IsBindingError(LogEventLevel level, string area) =>
        area == LogArea.Binding && level >= LogEventLevel.Warning;
}
