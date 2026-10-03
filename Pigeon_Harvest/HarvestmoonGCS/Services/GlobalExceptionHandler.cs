using System;
using System.Threading.Tasks;
using HarvestmoonGCS.Core.Services;
using Microsoft.UI.Xaml;

namespace HarvestmoonGCS.Services;

/// <summary>
/// Installs process-wide safety nets so an exception that escapes a UI handler cannot
/// hard-terminate the app (the "force close" symptom). Three sources are covered:
///   - UI-thread exceptions      → Application.UnhandledException
///   - background thread crashes → AppDomain.UnhandledException
///   - faulted fire-and-forget   → TaskScheduler.UnobservedTaskException
/// The previous version only subscribed to the first, so a faulted `_ = SomeAsync()` or a
/// background-thread throw still killed the process.
/// </summary>
public class GlobalExceptionHandler
{
    private readonly ILoggingService _logger;

    public GlobalExceptionHandler(ILoggingService logger)
    {
        _logger = logger;

        Application.Current.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _logger.LogError("Unhandled UI exception occurred", e.Exception);
        ShowErrorDialog("An unexpected error occurred. Please check the logs for details.");

        // Keep the app alive: the alternative is an immediate force close.
        e.Handled = true;
    }

    private void OnAppDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        // This is the last chance to record a background-thread crash. It is not recoverable
        // (the runtime is terminating), but logging it turns an opaque force close into a
        // diagnosable entry in moonharvest-*.log.
        try
        {
            if (e.ExceptionObject is Exception ex)
            {
                _logger.LogError("Unhandled background exception — process is terminating", ex);
            }
            else
            {
                _logger.LogError($"Unhandled background condition: {e.ExceptionObject}");
            }
        }
        catch
        {
            // Never let the crash logger itself throw.
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            _logger.LogError("Unobserved task exception", e.Exception);
            // Mark observed so a faulted fire-and-forget task does not escalate.
            e.SetObserved();
        }
        catch
        {
            // Ignore logger failures here.
        }
    }

    private void ShowErrorDialog(string message)
    {
        try
        {
            // Simplified for Uno Platform compatibility
            System.Diagnostics.Debug.WriteLine($"Error: {message}");
        }
        catch
        {
            // Fallback - ignore dialog errors
        }
    }
}
