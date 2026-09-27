using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LajmeBot.Ai;

/// <summary>
/// Writes articles through OpenRouter (OpenAI-compatible chat completions), so any model there can be used —
/// Gemini, DeepSeek, Qwen, GPT, Claude… Uses function calling for structured output and falls back to
/// plain JSON output for models without tool support.
/// </summary>
public sealed partial class OpenRouterWriter : IArticleWriter, IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(4) };
    private readonly AiConfig _cfg;
    private readonly Log _log;
    // "text" (plain labelled text — works with every model, weak models write longer articles this way),
    // "tools" (function calling) or "json" (response_format json_object). Falls back to "text" when needed.
    private string _mode;

    /// <summary>Total USD cost reported by OpenRouter during this run.</summary>
    public decimal TotalCost { get; private set; }

    public OpenRouterWriter(AiConfig cfg, string apiKey, Log log)
    {
        _cfg = cfg;
        _log = log;
        _mode = cfg.OutputFormat.Trim().ToLowerInvariant() switch { "tools" => "tools", "json" => "json", _ => "text" };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        // Optional attribution headers (shown in OpenRouter's app rankings).
        _http.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/bg20033/lmb");
        _http.DefaultRequestHeaders.Add("X-Title", "LajmeBot");
    }

    public async Task<ArticleDraft> WriteAsync(ArticleRequest req, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            var body = BuildRequest(req, _mode);
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var resp = await _http.PostAsync(_cfg.Endpoint + "/chat/completions", content, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            var status = (int)resp.StatusCode;

            if ((status == 429 || status >= 500) && attempt < 4)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt * attempt);
                _log.Warn($"OpenRouter {status}, retrying in {wait.TotalSeconds:0}s");
                await Task.Delay(wait, ct);
                continue;
            }
            if (!resp.IsSuccessStatusCode)
            {
                if (_mode != "text" && (ToolsUnsupported(text) || status == 400))
                {
                    _log.Warn($"{_cfg.Model}: '{_mode}' output not supported here ({status}) — switching to plain text output");
                    _mode = "text";
                    continue;
                }
                throw new InvalidOperationException($"OpenRouter {status}: {Truncate(text, 400)}");
            }

            var root = JsonNode.Parse(text)!;
            if (root["error"] is JsonNode err) // some upstream errors arrive with HTTP 200
            {
                if (attempt < 4) { _log.Warn($"OpenRouter error: {Truncate(err.ToJsonString(), 200)}; retrying"); await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct); continue; }
                throw new InvalidOperationException($"OpenRouter error: {Truncate(err.ToJsonString(), 400)}");
            }

            var choice = root["choices"]?[0];
            var finish = choice?["finish_reason"]?.GetValue<string>();
            var message = choice?["message"];
            var usage = root["usage"];
            _log.Debug($"model={root["model"]} tokens in={usage?["prompt_tokens"]} out={usage?["completion_tokens"]} cost={usage?["cost"]}");
            if (usage?["cost"] is JsonValue c && c.TryGetValue<decimal>(out var cost)) TotalCost += cost;
            if (finish == "length") throw new InvalidOperationException("The model hit max_tokens; raise Ai.MaxTokens");

            if (_mode == "text")
            {
                var draft = Prompts.ParseTextDraft(MessageText(message) ?? "");
                if (draft is not null) return draft;
                if (attempt < 3) { _log.Warn($"Model answer was not in the expected format (TITLE/…/BODY); retrying"); continue; }
                throw new InvalidOperationException($"Model answer not in the expected format (finish_reason={finish})");
            }
            var input = ExtractArguments(message);
            if (input is null)
            {
                if (attempt < 3) { _log.Warn("Model answered without usable JSON; switching to plain text output"); _mode = "text"; continue; }
                throw new InvalidOperationException($"Model returned no usable JSON (finish_reason={finish})");
            }
            return Prompts.ParseDraft(input);
        }
    }

    private JsonObject BuildRequest(ArticleRequest req, string mode)
    {
        var tool = Prompts.Tool(req.Site);
        var schema = tool["input_schema"]!.DeepClone();
        var system = Prompts.System(req.Site, _cfg);
        if (mode != "tools") system = system.Replace($"Always answer by calling the {Prompts.ToolName} tool.", "");
        if (mode == "json")
        {
            system += "\n\nOUTPUT FORMAT: reply with ONE JSON object and nothing else (no Markdown fences, no comments). " +
                      "It must match this JSON Schema:\n" + schema.ToJsonString();
        }
        else if (mode == "text")
        {
            system += "\n\n" + Prompts.TextFormat(req.Site, _cfg);
        }

        var body = new JsonObject
        {
            ["model"] = _cfg.Model,
            ["max_tokens"] = _cfg.MaxTokens,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = Prompts.User(req) }),
        };
        if (_cfg.Temperature is { } t) body["temperature"] = t;
        if (_cfg.FallbackModels.Count > 0)
            body["models"] = new JsonArray(new[] { _cfg.Model }.Concat(_cfg.FallbackModels).Distinct().Select(m => (JsonNode)JsonValue.Create(m)!).ToArray());

        if (mode == "json")
        {
            body["response_format"] = new JsonObject { ["type"] = "json_object" };
        }
        else if (mode == "tools")
        {
            body["tools"] = new JsonArray(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = Prompts.ToolName,
                    ["description"] = tool["description"]!.DeepClone(),
                    ["parameters"] = schema,
                },
            });
            body["tool_choice"] = new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = Prompts.ToolName } };
            // Only route to providers that really support tools/tool_choice for this model.
            body["provider"] = new JsonObject { ["require_parameters"] = true };
        }
        return body;
    }

    private static string? MessageText(JsonNode? message)
    {
        var content = message?["content"];
        return content?.GetValueKind() switch
        {
            JsonValueKind.String => content.GetValue<string>(),
            JsonValueKind.Array => string.Concat(content.AsArray().Select(p => p?["text"]?.GetValue<string>() ?? "")),
            _ => null,
        };
    }

    /// <summary>Gets the article JSON from a tool call, or from the message text in JSON mode.</summary>
    public static JsonObject? ExtractArguments(JsonNode? message)
    {
        if (message is null) return null;
        if (message["tool_calls"] is JsonArray calls)
        {
            foreach (var call in calls)
            {
                var args = call?["function"]?["arguments"];
                if (args is JsonObject obj) return obj;
                if (args?.GetValueKind() == JsonValueKind.String && TryParseObject(args.GetValue<string>()) is { } parsed) return parsed;
            }
        }
        var content = message["content"];
        string? text = content?.GetValueKind() switch
        {
            JsonValueKind.String => content.GetValue<string>(),
            JsonValueKind.Array => string.Concat(content.AsArray().Select(p => p?["text"]?.GetValue<string>() ?? "")),
            _ => null,
        };
        return text is null ? null : TryParseObject(text);
    }

    [GeneratedRegex(@"^```(?:json)?\s*|\s*```$", RegexOptions.Multiline)]
    private static partial Regex Fences();

    private static JsonObject? TryParseObject(string s)
    {
        s = Fences().Replace(s.Trim(), "").Trim();
        var start = s.IndexOf('{');
        var end = s.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonNode.Parse(s[start..(end + 1)]) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static bool ToolsUnsupported(string errorBody) =>
        Regex.IsMatch(errorBody, @"(tool|function)[^""]{0,80}(not supported|unsupported|does not support)|support tool use|No endpoints found that support", RegexOptions.IgnoreCase);

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";
    public void Dispose() => _http.Dispose();
}
