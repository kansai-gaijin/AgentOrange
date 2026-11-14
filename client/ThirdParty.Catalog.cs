using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AgentOrange;

/// <summary>
/// The services Agent Orange knows, and a free call for each that shows a key works.
/// How each one's API works is in docs/third-party-apis.md, for building workflow nodes.
/// </summary>
public sealed partial class ThirdParty
{
    private const string Group3D = "3D";
    private const string GroupImages = "Images";
    private const string GroupAudio = "Sound and music";

    public static readonly ThirdPartyService[] Catalog =
    [
        new("tripo", "Tripo", Group3D,
            "Text, image and multi-view to 3D, with texturing, rigging, retargeting, retopology and format conversion.",
            [ServiceUse.Models3D],
            [new("key", "API key", "Starts with tsk_. Made under API keys on Tripo's developer site.")],
            "https://developers.tripo3d.ai/en/keys",
            "https://developers.tripo3d.ai/en/docs/introduction"),
        new("hunyuan", "Tencent Hunyuan 3D", Group3D,
            "Text, image and multi-view to 3D (Pro and Rapid), with parts, smart topology, retexturing, UVs and auto rigging. Uses an international Tencent Cloud account with Hunyuan 3D turned on.",
            [ServiceUse.Models3D],
            [
                new("secret_id", "SecretId", "Starts with AKID. Made under API keys in Tencent Cloud's console.", Secret: false),
                new("secret_key", "SecretKey", "Shown once, when the key pair is made."),
                new("region", "Region", "Where requests go.", Secret: false, Options: ["ap-singapore", "eu-frankfurt", "na-siliconvalley"]),
            ],
            "https://console.tencentcloud.com/cam/capi",
            "https://www.tencentcloud.com/document/product/1284"),
        new("meshy", "Meshy", Group3D,
            "Text, image and multi-image to 3D, with retexturing, remeshing, UVs, rigging and animation. Making things needs a paid Meshy plan.",
            [ServiceUse.Models3D],
            [new("key", "API key", "Starts with msy_. Made under API keys on Meshy's site.")],
            "https://www.meshy.ai/developers/keys",
            "https://docs.meshy.ai"),
        new("openai", "OpenAI API", GroupImages,
            "GPT Image: pictures from a prompt, edits of your own pictures, and transparent backgrounds. Also speech for voices. Paid per use.",
            [ServiceUse.Images, ServiceUse.Voice],
            [new("key", "API key", "Starts with sk-. Made under API keys on OpenAI's platform site.")],
            "https://platform.openai.com/api-keys",
            "https://platform.openai.com/docs/guides/image-generation"),
        new("openai-chatgpt", "OpenAI with ChatGPT", GroupImages,
            "GPT Image through your ChatGPT plan instead of a paid key, by way of one of your Codex logins.",
            [ServiceUse.Images],
            [new("login", "Codex login", "The login whose ChatGPT plan pays for the pictures.", Secret: false)],
            "",
            "https://developers.openai.com/codex",
            ViaCodex: true),
        new("google", "Google Gemini", GroupImages,
            "Nano Banana image models: pictures from a prompt, edits, and several reference pictures at once. Also speech for voices.",
            [ServiceUse.Images, ServiceUse.Voice],
            [new("key", "API key", "Starts with AIza. Made in Google AI Studio.")],
            "https://aistudio.google.com/apikey",
            "https://ai.google.dev/gemini-api/docs/image-generation"),
        new("midjourney", "Midjourney", GroupImages,
            "Pictures with Midjourney's look.",
            [ServiceUse.Images],
            [],
            "",
            "https://www.midjourney.com",
            NoApi: "Midjourney has no public API, and its terms forbid automating its site or Discord bot. It can be added once Midjourney offers one."),
        new("elevenlabs", "ElevenLabs", GroupAudio,
            "Sound effects up to 30 seconds (looping too), music with or without vocals, and character voices and dialogue.",
            [ServiceUse.SoundEffects, ServiceUse.Music, ServiceUse.Voice],
            [new("key", "API key", "Made under API keys in ElevenLabs' settings. Shipping what it makes needs a paid plan.")],
            "https://elevenlabs.io/app/settings/api-keys",
            "https://elevenlabs.io/docs/api-reference/introduction"),
        new("suno", "Suno", GroupAudio,
            "Songs and instrumentals.",
            [ServiceUse.Music],
            [],
            "",
            "https://suno.com",
            NoApi: "Suno's developer API is a closed beta for chosen partners, and its terms forbid other ways of automating it. It can be added once Suno opens it. Until then ElevenLabs can make music."),
    ];

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Makes a free call with the saved key, to show it works. Nothing is made or charged.</summary>
    public async Task<ServiceCheck> CheckAsync(ThirdPartyService service)
    {
        try
        {
            return service.Id switch
            {
                "tripo" => await CheckTripoAsync(),
                "hunyuan" => await CheckHunyuanAsync(),
                "meshy" => await CheckMeshyAsync(),
                "openai" => await CheckSimpleAsync("https://api.openai.com/v1/models", "Authorization", $"Bearer {Read("openai", "key")}", "OpenAI"),
                "google" => await CheckSimpleAsync("https://generativelanguage.googleapis.com/v1beta/models", "x-goog-api-key", Read("google", "key"), "Google"),
                "elevenlabs" => await CheckElevenLabsAsync(),
                _ => new ServiceCheck(false, "There's no check for this one."),
            };
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            return new ServiceCheck(false, "Couldn't reach it. Check the internet connection and try again.");
        }
    }

    private static async Task<(HttpStatusCode Status, JsonNode? Body)> GetAsync(string url, string header, string? value)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation(header, value ?? "");
        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        JsonNode? body = null;
        try
        {
            body = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
        }
        return (response.StatusCode, body);
    }

    private static ServiceCheck Refused(HttpStatusCode status, string name) => status switch
    {
        HttpStatusCode.Unauthorized => new ServiceCheck(false, $"{name} turned the key down. Check it was copied whole, or make a new one."),
        HttpStatusCode.Forbidden => new ServiceCheck(false, $"{name} won't let this key do that. It may be limited to some uses or addresses."),
        HttpStatusCode.TooManyRequests => new ServiceCheck(false, $"{name} is busy. Try again in a minute."),
        _ => new ServiceCheck(false, $"{name} answered {(int)status}. Try again later."),
    };

    private static string Credits(JsonNode? value) =>
        value is null ? "" : value.GetValue<double>().ToString("#,0.##", CultureInfo.CurrentCulture);

    private static async Task<ServiceCheck> CheckSimpleAsync(string url, string header, string? value, string name)
    {
        var (status, _) = await GetAsync(url, header, value);
        return status == HttpStatusCode.OK ? new ServiceCheck(true, $"{name} took the key.") : Refused(status, name);
    }

    private async Task<ServiceCheck> CheckTripoAsync()
    {
        var (status, body) = await GetAsync("https://openapi.tripo3d.ai/v3/account/balance", "Authorization", $"Bearer {Read("tripo", "key")}");
        if (status != HttpStatusCode.OK || body?["data"] is not JsonObject data) return Refused(status, "Tripo");
        return new ServiceCheck(true, $"{Credits(data["balance"])} credits left.");
    }

    private async Task<ServiceCheck> CheckMeshyAsync()
    {
        var (status, body) = await GetAsync("https://api.meshy.ai/openapi/v1/balance", "Authorization", $"Bearer {Read("meshy", "key")}");
        if (status != HttpStatusCode.OK || body is not JsonObject data) return Refused(status, "Meshy");
        return new ServiceCheck(true, $"{Credits(data["balance"])} credits left. Making models needs a paid Meshy plan.");
    }

    private async Task<ServiceCheck> CheckElevenLabsAsync()
    {
        var (status, body) = await GetAsync("https://api.elevenlabs.io/v1/user/subscription", "xi-api-key", Read("elevenlabs", "key"));
        if (status != HttpStatusCode.OK || body is not JsonObject plan)
        {
            // A key can be limited to some endpoints, and still make sounds.
            return status == HttpStatusCode.Unauthorized && body?.ToJsonString().Contains("permission", StringComparison.OrdinalIgnoreCase) == true
                ? new ServiceCheck(true, "The key works, but can't show the plan. It may be limited to some endpoints.")
                : Refused(status, "ElevenLabs");
        }

        var left = (plan["character_limit"]?.GetValue<double>() ?? 0) - (plan["character_count"]?.GetValue<double>() ?? 0);
        var tier = plan["tier"]?.GetValue<string>() is { Length: > 0 } name ? $"{char.ToUpperInvariant(name[0])}{name[1..]} plan" : "Plan";
        return new ServiceCheck(true, $"{tier} · {left.ToString("#,0", CultureInfo.CurrentCulture)} credits left.");
    }

    /// <summary>
    /// Tencent has no free balance call for Hunyuan 3D, so this asks about a job that doesn't exist.
    /// A bad key pair is turned down before the job is looked up, and nothing is charged.
    /// </summary>
    private async Task<ServiceCheck> CheckHunyuanAsync()
    {
        var (_, body) = await CallTencentAsync("QueryHunyuanTo3DProJob", """{"JobId":"0"}""");
        if (body?["Response"]?["Error"]?["Code"]?.GetValue<string>() is not { } code) return new ServiceCheck(true, "Tencent Cloud took the key pair.");
        if (code.StartsWith("AuthFailure.SignatureExpire", StringComparison.Ordinal)) return new ServiceCheck(false, "Tencent turned it down because this PC's clock is off. Set the clock to update itself.");
        if (code.StartsWith("AuthFailure", StringComparison.Ordinal)) return new ServiceCheck(false, "Tencent Cloud turned the key pair down. Check both halves were copied whole.");
        if (code.Contains("NotActivated", StringComparison.Ordinal)) return new ServiceCheck(false, "The key pair works, but Hunyuan 3D isn't turned on for this account yet. Turn it on in Tencent Cloud's console.");
        if (code.Contains("Arrears", StringComparison.Ordinal)) return new ServiceCheck(false, "The key pair works, but the account is in arrears.");
        if (code == "UnsupportedRegion") return new ServiceCheck(false, "Hunyuan 3D isn't offered in that region. Pick another.");
        return new ServiceCheck(true, "Tencent Cloud took the key pair.");
    }

    /// <summary>Calls a Hunyuan action on the international Tencent Cloud, signed with TC3-HMAC-SHA256.</summary>
    internal async Task<(HttpStatusCode Status, JsonNode? Body)> CallTencentAsync(string action, string json)
    {
        const string host = "hunyuan.intl.tencentcloudapi.com";
        const string service = "hunyuan";
        const string contentType = "application/json; charset=utf-8";
        var secretId = Read("hunyuan", "secret_id") ?? "";
        var secretKey = Read("hunyuan", "secret_key") ?? "";
        var region = Read("hunyuan", "region") ?? "ap-singapore";

        var now = DateTimeOffset.UtcNow;
        var timestamp = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var date = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var body = Encoding.UTF8.GetBytes(json);

        var canonical = $"POST\n/\n\ncontent-type:{contentType}\nhost:{host}\n\ncontent-type;host\n{Hex(SHA256.HashData(body))}";
        var scope = $"{date}/{service}/tc3_request";
        var toSign = $"TC3-HMAC-SHA256\n{timestamp}\n{scope}\n{Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))}";
        var signing = Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("TC3" + secretKey), date), service), "tc3_request");
        var signature = Hex(Hmac(signing, toSign));

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/");
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        request.Headers.TryAddWithoutValidation("X-TC-Action", action);
        request.Headers.TryAddWithoutValidation("X-TC-Version", "2023-09-01");
        request.Headers.TryAddWithoutValidation("X-TC-Region", region);
        request.Headers.TryAddWithoutValidation("X-TC-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("Authorization", $"TC3-HMAC-SHA256 Credential={secretId}/{scope}, SignedHeaders=content-type;host, Signature={signature}");

        using var response = await Http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        try
        {
            return (response.StatusCode, JsonNode.Parse(text));
        }
        catch (System.Text.Json.JsonException)
        {
            return (response.StatusCode, null);
        }
    }

    private static byte[] Hmac(byte[] key, string text) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
