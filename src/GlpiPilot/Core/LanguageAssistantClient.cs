using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace GlpiPilot.Core;

/// <summary>One explicit compatible chat-completions request. No retries, redirects, cookies, tools or automatic requests.</summary>
public static class LanguageAssistantClient
{
    public static async Task<string> GenerateAsync(LanguageAssistantRequest request, string sessionKey, CancellationToken ct = default, HttpMessageHandler? handler = null)
    {
        request.Settings.Validate();
        if (request.Settings.Provider != "compatible" || !request.Settings.ExternalEnabled)
            throw new InvalidOperationException("Activez explicitement le fournisseur compatible et l’envoi au périmètre préparé avant de générer.");
        if (sessionKey is null || sessionKey.Length > 8000 || sessionKey.Any(char.IsControl)) throw new InvalidDataException("Clé de session invalide.");
        var endpoint = LanguageAssistantSettings.ValidateEndpoint(request.Settings.Endpoint);
        var key = sessionKey.Length > 0 ? sessionKey : LanguageAssistantStore.ReadKey(request.Settings);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            model = request.Settings.Model, store = false, max_completion_tokens = 6000,
            response_format = new { type = "json_object" },
            messages = new[] { new { role = "system", content = request.SystemMessage }, new { role = "user", content = request.UserMessage } }
        });
        if (payload.Length > LanguageAssistantService.MaximumRequestBytes) throw new InvalidDataException("La demande dépasse 256 Kio.");
        using var client = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, Credentials = null }, disposeHandler: handler is null)
        { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(request.Settings.TimeoutSeconds));
        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(payload) };
        message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        if (key.Length > 0) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        try
        {
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400) throw new InvalidDataException("Le fournisseur demande une redirection. Elle a été refusée : configurez explicitement l’adresse finale.");
            if (!response.IsSuccessStatusCode)
            {
                var hint = response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Vérifiez la clé et les autorisations du modèle.",
                    HttpStatusCode.TooManyRequests => "Le fournisseur limite les requêtes. Réessayez plus tard si vous le souhaitez.",
                    HttpStatusCode.BadRequest => "Vérifiez le modèle et la compatibilité chat/completions, JSON mode et max_completion_tokens.",
                    _ => "Vérifiez le fournisseur et sa disponibilité."
                };
                throw new InvalidDataException($"Le fournisseur a refusé la demande (HTTP {(int)response.StatusCode}). {hint} Aucun nouvel essai automatique.");
            }
            if (response.Content.Headers.ContentLength > LanguageAssistantService.MaximumResponseBytes) throw new InvalidDataException("La réponse dépasse 512 Kio.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream(); var block = new byte[8192];
            while (true)
            {
                var count = await stream.ReadAsync(block, timeout.Token).ConfigureAwait(false); if (count == 0) break;
                if (buffer.Length + count > LanguageAssistantService.MaximumResponseBytes) throw new InvalidDataException("La réponse dépasse 512 Kio.");
                buffer.Write(block, 0, count);
            }
            timeout.Token.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 24 });
            if (!document.RootElement.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1)
                throw new InvalidDataException("La réponse du fournisseur doit contenir une proposition unique.");
            var choice = choices[0];
            if (!choice.TryGetProperty("finish_reason", out var finish) || finish.ValueKind != JsonValueKind.String || finish.GetString() != "stop")
                throw new InvalidDataException("Le fournisseur n’a pas terminé une réponse complète (limite, filtre ou appel d’outil). Aucun fragment n’est appliqué.");
            if (!choice.TryGetProperty("message", out var reply) || reply.ValueKind != JsonValueKind.Object
                || reply.TryGetProperty("tool_calls", out _) || reply.TryGetProperty("function_call", out _)
                || reply.TryGetProperty("refusal", out var refusal) && refusal.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                || !reply.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Le fournisseur n’a pas renvoyé un contenu JSON textuel utilisable.");
            return content.GetString() ?? throw new InvalidDataException("Réponse vide.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Le délai choisi est dépassé. La demande est annulée localement ; le fournisseur peut avoir commencé son traitement. Aucun nouvel essai automatique."); }
        catch (JsonException error) { throw new InvalidDataException("La réponse du fournisseur n’est pas un objet chat/completions JSON valide.", error); }
    }
}
