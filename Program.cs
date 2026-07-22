using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

var apiKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.WriteLine("Defina a variável de ambiente GROQ_API_KEY antes de rodar.");
    return;
}

const string model = "openai/gpt-oss-20b";
const string url = "https://api.groq.com/openai/v1/chat/completions";
const int maxTurns = 3;
var projectRoot = Path.GetFullPath(args.Length > 0 ? args[0] : Directory.GetCurrentDirectory());
if (!Directory.Exists(projectRoot))
{
    Console.WriteLine($"Erro: a pasta '{projectRoot}' não existe.");
    return;
}
var historyPath = Path.Combine(projectRoot, ".console-history.json");

using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

var tools = JsonNode.Parse("""
[
  {"type":"function","function":{"name":"list_files","description":"Lista os arquivos e pastas dentro do projeto (recursivo), ignorando bin/obj/.git/node_modules. Use isso primeiro pra descobrir a estrutura do projeto antes de tentar ler um arquivo específico.","parameters":{"type":"object","properties":{"path":{"type":"string","description":"Subpasta relativa a listar, opcional — omita pra listar a partir da raiz do projeto."}},"required":[]}}},
  {"type":"function","function":{"name":"read_file","description":"Lê o conteúdo de um arquivo do projeto. Use line_start/line_end pra ler só um trecho de arquivos grandes, evitando gastar tokens à toa. Não releia um arquivo já lido nesta conversa sem necessidade.","parameters":{"type":"object","properties":{"path":{"type":"string","description":"Caminho relativo do arquivo."},"line_start":{"type":"integer","description":"Primeira linha (1-based), opcional."},"line_end":{"type":"integer","description":"Última linha (1-based), opcional."}},"required":["path"]}}},
  {"type":"function","function":{"name":"edit_file","description":"Edita um arquivo existente substituindo old_string (trecho pequeno, exato, único, copiado do read_file) por new_string. Nunca reescreva o arquivo inteiro.","parameters":{"type":"object","properties":{"path":{"type":"string","description":"Caminho relativo do arquivo."},"old_string":{"type":"string","description":"Trecho exato a substituir."},"new_string":{"type":"string","description":"Trecho novo."}},"required":["path","old_string","new_string"]}}},
  {"type":"function","function":{"name":"write_file","description":"Cria um arquivo NOVO (que ainda não existe). Para editar um arquivo existente, use edit_file.","parameters":{"type":"object","properties":{"path":{"type":"string","description":"Caminho relativo do arquivo."},"content":{"type":"string","description":"Conteúdo completo do novo arquivo."}},"required":["path","content"]}}}
]
""")!.AsArray();

var systemMessage = new JsonObject
{
    ["role"] = "system",
    ["content"] = "Você é um assistente que ajuda a editar código na pasta do projeto atual. " +
        "Se não souber quais arquivos existem no projeto, use list_files primeiro pra descobrir a estrutura. " +
        "Antes de editar um arquivo que já existe, SEMPRE chame read_file primeiro. " +
        "Para editar, use edit_file com old_string pequeno (só o trecho relevante) e new_string — nunca reescreva o arquivo inteiro. " +
        "Use write_file só para criar arquivo novo. Nunca invente conteúdo nem caminhos — use exatamente o que o usuário mencionou. " +
        "Depois de ler um arquivo, NUNCA cole o conteúdo inteiro na resposta — resuma ou cite só o trecho relevante."
};

var messages = File.Exists(historyPath)
    ? JsonNode.Parse(File.ReadAllText(historyPath))!.AsArray()
    : new JsonArray { systemMessage };
if (File.Exists(historyPath))
    Console.WriteLine($"Histórico anterior carregado ({messages.Count} mensagens).");

void SaveHistory() => File.WriteAllText(historyPath, messages.ToJsonString());

JsonArray RequestMessages()
{
    var turns = new List<List<JsonNode>>();
    for (var i = 1; i < messages.Count; i++)
    {
        if (turns.Count == 0 || messages[i]!["role"]!.GetValue<string>() == "user")
            turns.Add(new List<JsonNode>());
        turns[^1].Add(messages[i]!);
    }
    var result = new JsonArray { systemMessage.DeepClone() };
    foreach (var m in turns.Skip(Math.Max(0, turns.Count - maxTurns)).SelectMany(t => t))
        result.Add(m.DeepClone());
    return result;
}

bool Confirm(string label)
{
    Console.Write($"{label} [s/n] ");
    return string.Equals(Console.ReadLine()?.Trim(), "s", StringComparison.OrdinalIgnoreCase);
}

string[] IgnoredDirs = { "bin", "obj", ".git", "node_modules" };

string ExecuteTool(string name, JsonNode? args, string root)
{
    var path = args?["path"]?.GetValue<string>() ?? "";
    var fullPath = Path.GetFullPath(Path.Combine(root, path));
    if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        return $"Erro: acesso negado, '{path}' está fora da pasta do projeto.";

    if (name != "list_files" && string.IsNullOrWhiteSpace(path))
        return "Erro: parâmetro 'path' ausente.";

    try
    {
        switch (name)
        {
            case "list_files":
                if (!Directory.Exists(fullPath)) return $"Erro: pasta '{path}' não encontrada.";
                var entries = Directory.EnumerateFileSystemEntries(fullPath, "*", SearchOption.AllDirectories)
                    .Where(p => !p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(IgnoredDirs.Contains))
                    .Select(p => Path.GetRelativePath(root, p));
                return string.Join('\n', entries);

            case "read_file":
                if (!File.Exists(fullPath)) return $"Erro: arquivo '{path}' não encontrado.";
                var lines = File.ReadAllLines(fullPath);
                var start = Math.Max(1, args?["line_start"]?.GetValue<int>() ?? 1);
                var end = Math.Min(lines.Length, args?["line_end"]?.GetValue<int>() ?? lines.Length);
                return start > end || lines.Length == 0 ? "" : string.Join('\n', lines.Skip(start - 1).Take(end - start + 1));

            case "edit_file":
                if (!File.Exists(fullPath)) return $"Erro: arquivo '{path}' não encontrado. Use write_file para criar.";
                var oldStr = args?["old_string"]?.GetValue<string>();
                var newStr = args?["new_string"]?.GetValue<string>() ?? "";
                if (string.IsNullOrEmpty(oldStr)) return "Erro: 'old_string' ausente ou vazio.";

                var original = File.ReadAllText(fullPath);
                var count = (original.Length - original.Replace(oldStr, "").Length) / oldStr.Length;
                if (count == 0) return $"Erro: old_string não encontrado em '{path}'. Releia o arquivo e copie o trecho exato.";
                if (count > 1) return $"Erro: old_string aparece {count} vezes em '{path}'. Inclua mais contexto pra ficar único.";

                Console.WriteLine($"\n--- A IA quer editar: {path} ---\n- (removido):\n{oldStr}\n+ (adicionado):\n{newStr}\n--- fim da edição ---");
                if (!Confirm("Confirmar edição?")) return "Edição cancelada pelo usuário.";
                File.WriteAllText(fullPath, original.Replace(oldStr, newStr));
                return $"Arquivo '{path}' editado com sucesso.";

            case "write_file":
                if (File.Exists(fullPath)) return $"Erro: '{path}' já existe. Use edit_file para editar.";
                var content = args?["content"]?.GetValue<string>() ?? "";
                var dir = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) return $"Erro: a pasta '{Path.GetDirectoryName(path)}' não existe.";

                Console.WriteLine($"\n--- A IA quer gravar em: {path} ---\n{content}\n--- fim do conteúdo ---");
                if (!Confirm("Confirmar gravação?")) return "Gravação cancelada pelo usuário.";
                File.WriteAllText(fullPath, content);
                return $"Arquivo '{path}' gravado com sucesso.";

            default:
                return $"Erro: ferramenta '{name}' desconhecida.";
        }
    }
    catch (IOException ex)
    {
        return $"Erro de I/O em '{path}': {ex.Message}";
    }
}

Console.WriteLine("Console Groq (MVP). Pode pedir pra ler/editar arquivos da pasta do projeto. Digite 'exit' para sair.");

while (true)
{
    Console.Write("\n> ");
    var input = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(input) || input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
        break;

    messages.Add(new JsonObject { ["role"] = "user", ["content"] = input });
    SaveHistory();

    for (var round = 0; round < 10; round++)
    {
        JsonNode? responseJson = null;
        var failed = false;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                var response = await http.PostAsJsonAsync(url, new { model, messages = RequestMessages(), tools, tool_choice = "auto" });
                responseJson = await response.Content.ReadFromJsonAsync<JsonNode>();
                if (response.IsSuccessStatusCode) { failed = false; break; }

                failed = true;
                if ((int)response.StatusCode is 503 or 429 && attempt < 3)
                {
                    Console.WriteLine($"Serviço sobrecarregado ({(int)response.StatusCode}), tentando de novo em {attempt * 3}s... (tentativa {attempt}/3)");
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 3));
                    continue;
                }
                Console.WriteLine($"Erro {(int)response.StatusCode}: {responseJson}");
            }
            catch (TaskCanceledException)
            {
                Console.WriteLine("Timeout: sem resposta em 60s. Pode ser lentidão pontual da rede — tente de novo.");
                failed = true;
                break;
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"Falha de conexão: {ex.Message}");
                failed = true;
                break;
            }
        }

        if (failed) break;

        var message = responseJson?["choices"]?[0]?["message"];
        if (message is null) { Console.WriteLine("Resposta vazia da API."); break; }

        messages.Add(message.DeepClone());
        SaveHistory();

        var toolCalls = message["tool_calls"]?.AsArray();
        if (toolCalls is null || toolCalls.Count == 0)
        {
            Console.WriteLine(message["content"]?.GetValue<string>() ?? "");
            break;
        }

        foreach (var call in toolCalls)
        {
            var id = call!["id"]!.GetValue<string>();
            var fn = call["function"]!;
            var result = ExecuteTool(fn["name"]!.GetValue<string>(), JsonNode.Parse(fn["arguments"]!.GetValue<string>()), projectRoot);
            messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = result });
            SaveHistory();
        }
    }
}
