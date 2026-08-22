namespace Neadocs.Engine.Infrastructure.Http;

using System;
using System.Net.Http;
using System.Threading.Tasks;

public static class HealthProbe
{
    public const int Ok = 0;

    public const int Unhealthy = 1;

    public static async Task<int> RunAsync()
    {
        Uri target = ResolveTarget();

        try
        {
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
            using HttpResponseMessage response = await client.GetAsync(target);

            if (response.IsSuccessStatusCode)
            {
                return Ok;
            }

            Console.Error.WriteLine($"{target} returned {(int)response.StatusCode}.");

            return Unhealthy;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{target} was unreachable: {ex.Message}");

            return Unhealthy;
        }
    }

    public static Uri ResolveTarget()
    {
        string? urls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        int port = 5700;

        if (!string.IsNullOrWhiteSpace(urls))
        {
            string first = urls.Split(';', StringSplitOptions.RemoveEmptyEntries)[0];
            int colon = first.LastIndexOf(':');

            if (colon >= 0 && int.TryParse(first[(colon + 1)..].TrimEnd('/'), out int parsed))
            {
                port = parsed;
            }
        }

        return new Uri($"http://127.0.0.1:{port}/ready");
    }
}
