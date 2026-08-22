namespace Neadocs.Engine.Infrastructure.Http;

public sealed class StatusResponse
{
    public StatusResponse()
    {
    }

    public StatusResponse(string status) => Status = status;

    public string Status { get; set; } = string.Empty;
}
