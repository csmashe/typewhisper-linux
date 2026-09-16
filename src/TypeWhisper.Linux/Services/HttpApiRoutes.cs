namespace TypeWhisper.Linux.Services;

internal static class HttpApiRoutes
{
    internal static IReadOnlyList<(string Method, string Path, string Description)> Descriptions { get; } =
        Array.AsReadOnly<(string, string, string)>([
            ("GET", "/docs", "Read the HTTP API documentation (no token required)."),
            ("GET", "/docs/", "Read the HTTP API documentation (no token required)."),
            ("GET", "/v1/status", "Read service readiness and active model information."),
            ("GET", "/v1/capabilities", "Discover routes, response formats, and upload limits."),
            ("GET", "/v1/models", "List available transcription models."),
            ("POST", "/v1/models/load", "Load an engine model, optionally waiting for its download."),
            ("POST", "/v1/models/unload", "Unload the active engine model."),
            ("DELETE", "/v1/models", "Delete downloaded files for an unselected, unloaded model."),
            ("POST", "/v1/transcribe", "Transcribe a multipart upload or raw audio body."),
            ("POST", "/v1/transcribe/local-file", "Transcribe a local file specified by a JSON path."),
            ("GET", "/v1/history", "Search transcription history with q, limit, and offset."),
            ("DELETE", "/v1/history", "Delete a history record by id."),
            ("GET", "/v1/profiles", "List profiles under both rules and profiles."),
            ("PUT", "/v1/profiles/toggle", "Toggle a profile by id."),
            ("GET", "/v1/rules", "Alias for the profiles list."),
            ("PUT", "/v1/rules/toggle", "Alias for toggling a profile by id."),
            ("POST", "/v1/dictation/start", "Start a dictation session."),
            ("POST", "/v1/dictation/stop", "Stop the active dictation session."),
            ("GET", "/v1/dictation/status", "Read dictation state."),
            ("GET", "/v1/dictation/transcription", "Read a dictation session result."),
            ("GET", "/v1/dictionary/terms", "List dictionary terms."),
            ("PUT", "/v1/dictionary/terms", "Add dictionary terms."),
            ("DELETE", "/v1/dictionary/terms", "Delete a dictionary term."),
            ("GET", "/v1/dictionary/corrections", "List dictionary corrections."),
            ("PUT", "/v1/dictionary/corrections", "Add or update a dictionary correction."),
            ("DELETE", "/v1/dictionary/corrections", "Delete a dictionary correction."),
        ]);

    public static IReadOnlyList<(string Method, string Path)> All { get; } =
        Array.AsReadOnly(Descriptions.Select(route => (route.Method, route.Path)).ToArray());

    public static bool Contains(string method, string path) => All.Contains((method, path));
}
