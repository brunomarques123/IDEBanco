using System.Net.Http.Json;
using System.Text.Json.Nodes;

var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("Defina a variável de ambiente GEMINI_API_KEY antes de rodar.");
    return;
}

const string model = "gemini-flash-latest";
var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
http.DefaultRequestHeaders.Add("x-goog-api-key", apiKey);

Console.WriteLine("Console Gemini (MVP). Digite 'exit' para sair.");

while (true)
{
    Console.Write("\n> ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input) || input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
        break;

    var body = new
    {
        contents = new[]
        {
            new { parts = new[] { new { text = input } } }
        }
    };

    try
    {
        var response = await http.PostAsJsonAsync(url, body);
        var json = await response.Content.ReadFromJsonAsync<JsonNode>();

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Erro {(int)response.StatusCode}: {json}");
            continue;
        }

        var text = json?["candidates"]?[0]?["content"]?["parts"]?[0]?["text"]?.GetValue<string>();
        Console.WriteLine(text);
    }
    catch (TaskCanceledException)
    {
        Console.WriteLine("Timeout: sem resposta em 20s. Provável bloqueio de rede/firewall para generativelanguage.googleapis.com.");
    }
    catch (HttpRequestException ex)
    {
        Console.WriteLine($"Falha de conexão: {ex.Message}");
    }
}
