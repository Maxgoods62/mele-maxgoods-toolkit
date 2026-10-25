using ModelContextProtocol;

namespace MeleMcp.Tools;

internal static class ToolErrors
{
    /// <summary>
    /// The SDK replaces non-McpException messages with a generic "An error occurred" text. This server is local, so the real
    /// exception type and message go back to Claude, which needs them to correct its next call.
    /// </summary>
    public static T Run<T>(Func<T> body)
    {
        try
        {
            return body();
        }
        catch (Exception ex) when (ex is not McpException and not OperationCanceledException)
        {
            throw new McpException($"{ex.GetType().Name}: {ex.Message}", ex);
        }
    }
}
