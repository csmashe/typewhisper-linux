namespace TypeWhisper.Linux.Services;

internal sealed record HttpApiResponse(int StatusCode, string Body, string ContentType = "application/json")
{
    public static implicit operator HttpApiResponse((int StatusCode, string Body) value) =>
        new(value.StatusCode, value.Body);
}
