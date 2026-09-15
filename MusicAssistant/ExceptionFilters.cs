namespace MusicAssistant;

/// <summary>
/// Exception filters for the few places that must contain any failure.
/// </summary>
/// <remarks>
/// The C# coding conventions ask code not to catch <see cref="Exception"/> without a filter. Most catch blocks in this
/// app name the exceptions they expect. A handful of boundaries can't: a background thread's entry point, a UI event
/// handler that calls into the network, audio and Windows APIs at once, best-effort cleanup around third-party
/// libraries, and logging itself. Those use <c>catch (Exception ex) when (ExceptionFilters.IsRecoverable(ex))</c>, so
/// failures the process can't survive still surface instead of being swallowed.
/// </remarks>
public static class ExceptionFilters
{
    /// <summary>
    /// Returns whether an exception leaves the process in a state where carrying on is safe.
    /// </summary>
    /// <param name="exception">The exception being considered by a catch block.</param>
    /// <returns><see langword="false"/> for out-of-memory, stack exhaustion and memory corruption; otherwise <see langword="true"/>.</returns>
    public static bool IsRecoverable(Exception exception)
    {
        return exception is not (OutOfMemoryException or InsufficientExecutionStackException or AccessViolationException);
    }
}
