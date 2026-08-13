using Microsoft.AspNetCore.Mvc;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Backend.Controllers;

[ApiController]
[Route("api/google")]
public class GoogleApiController : ControllerBase
{
    /// <summary>GET /api/google/status - Returns whether Google API key is configured (no external call).</summary>
    [HttpGet("status")]
    public IActionResult Status()
    {
        var key = Services.GoogleApiKeyHelper.GetGoogleApiKey();
        var configured = !string.IsNullOrWhiteSpace(key);
        var mapsConfigured = !string.IsNullOrWhiteSpace(Services.GoogleApiKeyHelper.GetMapsApiKey());
        return Ok(new
        {
            configured,
            mapsConfigured,
            message = configured ? "Google API key is set. Gemini uses GOOGLE_API_KEY; Maps, Places, Directions, Geocoding, and Speech-to-Text use GOOGLE_MAPS_API_KEY." : "Google API key is not set. Add GOOGLE_API_KEY in Railway environment variables."
        });
    }

    /// <summary>GET /api/google/health - Calls Gemini with a minimal prompt (legacy).</summary>
    [HttpGet("health")]
    public Task<IActionResult> Health() => Gemini();

    /// <summary>GET /api/google/gemini - Test Gemini API.</summary>
    [HttpGet("gemini")]
    public async Task<IActionResult> Gemini()
    {
        var key = Services.GoogleApiKeyHelper.GetGoogleApiKey();
        if (string.IsNullOrWhiteSpace(key))
            return Ok(new { status = "not_configured", message = "GOOGLE_API_KEY is not set.", service = "Gemini" });

        try
        {
            var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={Uri.EscapeDataString(key)}";
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            var body = new { contents = new[] { new { parts = new[] { new { text = "Reply with exactly: OK" } } } } };
            var json = JsonSerializer.Serialize(body);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(url, content);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return Ok(new { status = "error", message = responseText.Length > 200 ? responseText.Substring(0, 200) + "..." : responseText, service = "Gemini" });

            using var doc = JsonDocument.Parse(responseText);
            var root = doc.RootElement;
            string message = "OK";
            if (root.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0)
            {
                var first = candidates.EnumerateArray().FirstOrDefault();
                if (first.ValueKind != System.Text.Json.JsonValueKind.Undefined && first.TryGetProperty("content", out var contentNode) && contentNode.TryGetProperty("parts", out var parts))
                {
                    var firstPart = parts.EnumerateArray().FirstOrDefault();
                    if (firstPart.ValueKind != System.Text.Json.JsonValueKind.Undefined && firstPart.TryGetProperty("text", out var text))
                        message = text.GetString()?.Trim() ?? "OK";
                }
            }
            return Ok(new { status = "ok", message, service = "Gemini" });
        }
        catch (Exception ex)
        {
            return Ok(new { status = "error", message = ex.Message, service = "Gemini" });
        }
    }

    /// <summary>GET /api/google/geocoding - Test Geocoding API (addresses to coordinates).</summary>
    [HttpGet("geocoding")]
    public async Task<IActionResult> Geocoding()
    {
        var key = Services.GoogleApiKeyHelper.GetMapsApiKey();
        if (string.IsNullOrWhiteSpace(key))
            return Ok(new { status = "not_configured", message = "GOOGLE_MAPS_API_KEY is not set.", service = "Geocoding" });

        try
        {
            var url = $"https://maps.googleapis.com/maps/api/geocode/json?address=Times+Square+New+York&key={Uri.EscapeDataString(key)}";
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            var response = await httpClient.GetAsync(url);
            var responseText = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return Ok(new { status = "error", message = responseText.Length > 200 ? responseText.Substring(0, 200) + "..." : responseText, service = "Geocoding" });
            using var doc = JsonDocument.Parse(responseText);
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : "";
            if (status == "OK")
                return Ok(new { status = "ok", message = "Geocoding API responded successfully.", service = "Geocoding" });
            return Ok(new { status = "error", message = status, service = "Geocoding" });
        }
        catch (Exception ex)
        {
            return Ok(new { status = "error", message = ex.Message, service = "Geocoding" });
        }
    }

    /// <summary>GET /api/google/maps - Test Maps JavaScript API (loader URL).</summary>
    [HttpGet("maps")]
    public async Task<IActionResult> Maps()
    {
        var key = Services.GoogleApiKeyHelper.GetMapsApiKey();
        if (string.IsNullOrWhiteSpace(key))
            return Ok(new { status = "not_configured", message = "GOOGLE_MAPS_API_KEY is not set.", service = "Maps" });

        try
        {
            var url = $"https://maps.googleapis.com/maps/api/js?key={Uri.EscapeDataString(key)}";
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            var response = await httpClient.GetAsync(url);
            var responseText = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return Ok(new { status = "error", message = responseText.Length > 200 ? responseText.Substring(0, 200) + "..." : responseText, service = "Maps" });
            if (responseText.Contains("ApiNotActivatedMapError") || responseText.Contains("RefererNotAllowedMapError") || responseText.Contains("InvalidKeyMapError"))
                return Ok(new { status = "error", message = responseText.Contains("ApiNotActivatedMapError") ? "Maps JavaScript API is not enabled for this key." : (responseText.Contains("RefererNotAllowedMapError") ? "Referer not allowed for this key." : "Invalid API key." ), service = "Maps" });
            return Ok(new { status = "ok", message = "Maps JavaScript API key valid.", service = "Maps" });
        }
        catch (Exception ex)
        {
            return Ok(new { status = "error", message = ex.Message, service = "Maps" });
        }
    }

    /// <summary>GET /api/google/directions - Test Directions API (routes between addresses).</summary>
    [HttpGet("directions")]
    public async Task<IActionResult> Directions()
    {
        var key = Services.GoogleApiKeyHelper.GetMapsApiKey();
        if (string.IsNullOrWhiteSpace(key))
            return Ok(new { status = "not_configured", message = "GOOGLE_MAPS_API_KEY is not set.", service = "Directions" });

        try
        {
            var origin = Uri.EscapeDataString("Times Square, New York, NY");
            var dest = Uri.EscapeDataString("Brooklyn Bridge, New York, NY");
            var url = $"https://maps.googleapis.com/maps/api/directions/json?origin={origin}&destination={dest}&key={Uri.EscapeDataString(key)}";
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            var response = await httpClient.GetAsync(url);
            var responseText = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return Ok(new { status = "error", message = responseText.Length > 200 ? responseText.Substring(0, 200) + "..." : responseText, service = "Directions" });
            using var doc = JsonDocument.Parse(responseText);
            var status = doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : "";
            if (status == "OK")
                return Ok(new { status = "ok", message = "Directions API responded successfully. Use it from the backend to return routes to the frontend.", service = "Directions" });
            return Ok(new { status = "error", message = status, service = "Directions" });
        }
        catch (Exception ex)
        {
            return Ok(new { status = "error", message = ex.Message, service = "Directions" });
        }
    }

    /// <summary>GET /api/google/places - Test Places API (New): search places.</summary>
    [HttpGet("places")]
    public async Task<IActionResult> Places()
    {
        var key = Services.GoogleApiKeyHelper.GetMapsApiKey();
        if (string.IsNullOrWhiteSpace(key))
            return Ok(new { status = "not_configured", message = "GOOGLE_MAPS_API_KEY is not set.", service = "Places" });

        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            httpClient.DefaultRequestHeaders.Add("X-Goog-Api-Key", key);
            httpClient.DefaultRequestHeaders.Add("X-Goog-FieldMask", "places.id");
            var body = JsonSerializer.Serialize(new { textQuery = "coffee" });
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync("https://places.googleapis.com/v1/places:searchText", content);
            var responseText = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
                return Ok(new { status = "ok", message = "Places API (New) responded successfully.", service = "Places" });
            return Ok(new { status = "error", message = responseText.Length > 200 ? responseText.Substring(0, 200) + "..." : responseText, service = "Places" });
        }
        catch (Exception ex)
        {
            return Ok(new { status = "error", message = ex.Message, service = "Places" });
        }
    }

    /// <summary>GET /api/google/speech-to-text - Test Cloud Speech-to-Text API with minimal audio.</summary>
    [HttpGet("speech-to-text")]
    public async Task<IActionResult> SpeechToText()
    {
        var key = Services.GoogleApiKeyHelper.GetMapsApiKey();
        if (string.IsNullOrWhiteSpace(key))
            return Ok(new { status = "not_configured", message = "GOOGLE_MAPS_API_KEY is not set.", service = "SpeechToText" });

        try
        {
            var silenceBytes = new byte[3200];
            var base64Audio = Convert.ToBase64String(silenceBytes);
            var body = JsonSerializer.Serialize(new
            {
                config = new { encoding = "LINEAR16", sampleRateHertz = 16000, languageCode = "en-US" },
                audio = new { content = base64Audio }
            });
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(10);
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync($"https://speech.googleapis.com/v1/speech:recognize?key={Uri.EscapeDataString(key)}", content);
            var responseText = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode)
                return Ok(new { status = "ok", message = "Speech-to-Text API accepted the request.", service = "SpeechToText" });
            if (response.StatusCode == System.Net.HttpStatusCode.BadRequest && responseText.Contains("No speech"))
                return Ok(new { status = "ok", message = "Speech-to-Text API responded (no speech in test audio).", service = "SpeechToText" });
            return Ok(new { status = "error", message = responseText.Length > 200 ? responseText.Substring(0, 200) + "..." : responseText, service = "SpeechToText" });
        }
        catch (Exception ex)
        {
            return Ok(new { status = "error", message = ex.Message, service = "SpeechToText" });
        }
    }
}
