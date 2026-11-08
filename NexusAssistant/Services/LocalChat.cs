using System.Net.Http.Json;
using System.Text.Json;

namespace NexusAssistant.Services;

public interface ILocalChat
{
    Task<Reply> AnswerAsync(string input, string model, IReadOnlyList<ChatMessage> history, CancellationToken token);
}

public sealed class LocalChat(HttpClient client) : ILocalChat
{
    public async Task<Reply> AnswerAsync(string input, string model, IReadOnlyList<ChatMessage> history, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return input.Trim().TrimEnd('!', '.', '?').ToLowerInvariant() switch
            {
                "привет" or "здравствуй" or "здравствуйте" => new("Привет! Я могу открыть приложение, помочь с папками и управлять Яндекс Музыкой."),
                "спасибо" => new("Пожалуйста!"),
                _ => new("Не удалось определить команду. В разделе «Команды» есть примеры. Для свободного диалога укажите имя установленной модели Ollama в настройках.")
            };
        }

        var messages = new List<object> { new { role = "system", content = "Ты NEXUS Assistant. Отвечай по-русски и кратко. Не утверждай, что выполнил действия на компьютере. Твои ответы являются только текстом." } };
        messages.AddRange(history.TakeLast(12).Select(message => new
        {
            role = message.IsUser ? "user" : "assistant",
            content = message.Text
        }));
        messages.Add(new { role = "user", content = input });

        using var response = await client.PostAsJsonAsync("http://127.0.0.1:11434/api/chat",
            new { model = model.Trim(), messages, stream = false }, token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new($"Модель «{model}» не найдена в Ollama. Проверьте имя в настройках.", true);
        response.EnsureSuccessStatusCode();
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
        var answer = body.RootElement.GetProperty("message").GetProperty("content").GetString();
        return string.IsNullOrWhiteSpace(answer) ? new("Модель вернула пустой ответ.", true) : new(answer.Trim());
    }
}
