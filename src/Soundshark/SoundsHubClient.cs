using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Soundshark;

public sealed record SoundsHubSource(string Url, string PublishableKey)
{
    public Uri Origin
    {
        get
        {
            if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 ||
                string.IsNullOrWhiteSpace(PublishableKey) || PublishableKey.StartsWith("sb_secret_", StringComparison.Ordinal))
                throw new InvalidDataException("Sounds Hub needs a valid public service configuration.");
            if (!PublishableKey.StartsWith("sb_publishable_", StringComparison.Ordinal))
                throw new InvalidDataException("Use a Supabase publishable key for Sounds Hub, never a secret or service-role key.");
            return uri;
        }
    }
    public static SoundsHubSource? Load()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "hub-source.json");
        if (!File.Exists(path)) return null;
        var source = JsonSerializer.Deserialize<SoundsHubSource>(File.ReadAllText(path)) ?? throw new InvalidDataException("Invalid Sounds Hub configuration.");
        _ = source.Origin; return source;
    }
}

public sealed record HubSound(Guid Id, string Title, string Uploader, double Duration, long Bytes, string Sha256, Guid OwnerId, bool UploaderIsOwner = false, string? CountryCode = null)
{
    public string Detail => $"{Uploader}  /  {Duration:F1}s  /  {Bytes / 1000.0:F0} KB";
    [JsonIgnore] public System.Windows.Media.ImageSource? CountryFlag => CountryCode == null ? null : HubCountries.Flag(CountryCode);
    [JsonIgnore] public System.Windows.Visibility CountryFlagVisibility => CountryCode == null ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public void Validate()
    {
        if (Id == Guid.Empty || OwnerId == Guid.Empty || string.IsNullOrWhiteSpace(Title) || Title.Length > 80 ||
            string.IsNullOrWhiteSpace(Uploader) || Uploader.Length > 40 || !double.IsFinite(Duration) || Duration is <= 0 or > 60.1 ||
            Bytes is <= 0 or > SoundsHubClient.MaxAudioBytes || Sha256 == null || Sha256.Length != 64 || !Sha256.All(Uri.IsHexDigit) ||
            CountryCode != null && !HubCountries.Supports(CountryCode))
            throw new InvalidDataException("Sounds Hub returned invalid sound details.");
    }
}
public sealed record HubPage(HubSound[] Sounds, bool HasMore);
public sealed record HubCountryPreference(string? CountryCode, bool ShowCountry)
{
    public void Validate()
    {
        if (ShowCountry && !HubCountries.Supports(CountryCode)) throw new InvalidDataException("Choose a supported country or region before displaying its flag.");
        if (CountryCode != null && !HubCountries.Supports(CountryCode)) throw new InvalidDataException("Choose a supported country or region.");
    }
}
public sealed record HubSession(string AccessToken, string RefreshToken, Guid UserId, string DisplayName, long ExpiresAt);
public sealed record HubRememberedSession(HubSession Session, long RememberUntil);
public sealed record HubRecovery(Guid ChallengeId);
public sealed record HubRecoveryResult(bool Verified, int Remaining);

public sealed class SoundsHubClient : IDisposable
{
    public const int MaxAudioBytes = 10 * 1024 * 1024;
    static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    readonly HttpClient http;
    readonly Uri origin;
    readonly string key;
    readonly string? sessionPath;
    readonly SemaphoreSlim authGate = new(1);
    readonly Func<DateTimeOffset> now;
    bool rememberLogin;
    long rememberUntil;
    HubSession? recoverySession;
    public HubSession? Session { get; private set; }
    public bool IsOwner { get; private set; }
    public event Action? AccountChanged;
    public async Task RefreshRole(CancellationToken ct)
    {
        IsOwner = false; AccountChanged?.Invoke();
        if (Session == null) return;
        await Refresh(ct);
        using var result = await Send(Request(HttpMethod.Get, "/functions/v1/sounds-hub/account", true), ct);
        IsOwner = result.RootElement.GetProperty("isOwner").GetBoolean(); AccountChanged?.Invoke();
    }
    public async Task<Guid> SubmitIssue(string message, HubLogFile[] files, CancellationToken ct, string kind = "issue")
    {
        if (message.Trim().Length is < 10 or > 8000 || files.Length > 5) throw new InvalidDataException("Enter 10–8000 characters and attach up to five logs.");
        await Refresh(ct); var request = Request(HttpMethod.Post, "/functions/v1/sounds-hub/issues", true);
        request.Content = JsonContent.Create(new { message, files, kind }); using var result = await Send(request, ct);
        return result.RootElement.GetProperty("id").GetGuid();
    }
    public async Task<HubReportPage> Reports(string status, int page, CancellationToken ct)
    {
        await Refresh(ct); using var result = await Send(Request(HttpMethod.Get, "/functions/v1/sounds-hub/admin/reports?status="+Uri.EscapeDataString(status)+"&page="+page, true),ct);
        return result.RootElement.Deserialize<HubReportPage>(Json) ?? throw new InvalidDataException("No report list returned.");
    }
    public async Task<HubSupportReport> ReportDetails(Guid id, CancellationToken ct)
    {
        await Refresh(ct); using var request = Request(HttpMethod.Get, "/functions/v1/sounds-hub/admin/reports/"+id,true);
        using var response = await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,ct);
        await EnsureSuccess(response,ct);
        return JsonSerializer.Deserialize<HubSupportReport>(await ReadBounded(await response.Content.ReadAsStreamAsync(ct),3*1024*1024,ct),Json) ?? throw new InvalidDataException("Report not found.");
    }
    public async Task SetReportStatus(Guid id, string status, CancellationToken ct)
    {
        await Refresh(ct); var request = Request(HttpMethod.Patch,"/functions/v1/sounds-hub/admin/reports/"+id,true);
        request.Content = JsonContent.Create(new { status }); using var result = await Send(request,ct);
    }
    public SoundsHubClient(SoundsHubSource source, HttpMessageHandler? handler = null, string? sessionFile = null, Func<DateTimeOffset>? clock = null)
    {
        origin = source.Origin; key = source.PublishableKey;
        http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };
        sessionPath = sessionFile;
        now = clock ?? (() => DateTimeOffset.UtcNow);
        if (sessionPath != null && File.Exists(sessionPath))
        {
            try {
                var saved = JsonSerializer.Deserialize<HubRememberedSession>(ProtectedData.Unprotect(File.ReadAllBytes(sessionPath), Encoding.UTF8.GetBytes(origin.AbsoluteUri), DataProtectionScope.CurrentUser), Json);
                if (saved?.Session != null && saved.RememberUntil > now().ToUnixTimeSeconds()) {
                    Session = saved.Session; rememberUntil = saved.RememberUntil; rememberLogin = true;
                } else File.Delete(sessionPath);
            }
            catch (Exception ex) when (ex is IOException or CryptographicException or JsonException) { Session = null; }
        }
    }
    HttpRequestMessage Request(HttpMethod method, string path, bool authenticated = false)
    {
        var request = new HttpRequestMessage(method, new Uri(origin, path));
        request.Headers.Add("apikey", key);
        if (authenticated)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Session?.AccessToken ?? throw new InvalidOperationException("Sign in to Sounds Hub first."));
        return request;
    }
    public static async Task<byte[]> ReadBounded(Stream stream, int limit, CancellationToken ct)
    {
        using var memory = new MemoryStream(); var buffer = new byte[32768];
        while (true)
        {
            int length = await stream.ReadAsync(buffer, ct);
            if (length == 0) return memory.ToArray();
            if (memory.Length + length > limit) throw new InvalidDataException("The Sounds Hub response exceeded its size limit.");
            await memory.WriteAsync(buffer.AsMemory(0, length), ct);
        }
    }
    async Task<JsonDocument> Send(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized && request.Headers.Authorization != null) SaveSession(null);
            if (response.StatusCode == HttpStatusCode.NotFound && request.RequestUri!.AbsolutePath.StartsWith("/functions/v1/sounds-hub/auth/", StringComparison.Ordinal))
                throw new InvalidDataException("Username sign-in and password recovery are not available on this Hub service yet. Email sign-in is available.");
            if (response.StatusCode == HttpStatusCode.NotFound && request.RequestUri!.AbsolutePath.EndsWith("/profile", StringComparison.Ordinal))
                throw new HttpRequestException("Country flags need the updated Hub service deployed first.", null, HttpStatusCode.NotFound);
            await EnsureSuccess(response, ct);
            return JsonDocument.Parse(await ReadBounded(await response.Content.ReadAsStreamAsync(ct), 1024 * 1024, ct));
        }
    }
    static async Task EnsureSuccess(HttpResponseMessage response, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (response.IsSuccessStatusCode) return;
        string? code = null;
        try {
            using var body = JsonDocument.Parse(await ReadBounded(await response.Content.ReadAsStreamAsync(ct), 65536, ct));
            if (body.RootElement.ValueKind == JsonValueKind.Object) {
                if (body.RootElement.TryGetProperty("error_code", out var error) && error.ValueKind == JsonValueKind.String) code = error.GetString();
                else if (body.RootElement.TryGetProperty("code", out error) && error.ValueKind == JsonValueKind.String) code = error.GetString();
            }
        } catch (JsonException) { }
        string message = code switch {
            "suggestion_limit" => "You can send 5 suggestions per 24 hours. Please try again later.",
            "report_limit" => "You can send 6 reports per 12 hours. Please try again later.",
            "same_password" => "That is your current password. Choose a different password.",
            "weak_password" => "That password does not meet the account requirements. Use 6–12 characters.",
            "email_exists" or "user_already_exists" => "An account with that email already exists. Sign in or reset your password.",
            "email_not_confirmed" => "Verify your email before signing in.",
            "invalid_credentials" => "Incorrect username or email, or password.",
            "otp_expired" => "That code is invalid or has expired. Check your email and try again.",
            "over_email_send_rate_limit" => "Please wait at least 60 seconds before requesting another email.",
            _ => response.StatusCode switch {
            HttpStatusCode.Unauthorized => "Please sign in again. Your session may have expired.",
            HttpStatusCode.Forbidden => "This action is not allowed for your account.",
            HttpStatusCode.TooManyRequests => "Too many requests. Please wait before trying again.",
            HttpStatusCode.NotFound when response.RequestMessage?.RequestUri?.AbsolutePath.Contains("/admin/") == true || response.RequestMessage?.RequestUri?.AbsolutePath.EndsWith("/issues") == true => "Owner tools and reports need the updated Hub service deployed first.",
            HttpStatusCode.NotFound when response.RequestMessage?.RequestUri?.AbsolutePath.EndsWith("/profile") == true => "Country flags need the updated Hub service deployed first.",
            HttpStatusCode.NotFound => "This sound is no longer available.",
            HttpStatusCode.Gone => "This recovery attempt has ended. Start password recovery again.",
            HttpStatusCode.RequestEntityTooLarge => "Choose audio smaller than 10 MB and shorter than 60 seconds.",
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "Check the details you entered. For sign-in, check your email, password, or verification code.",
            _ => "Sounds Hub is unavailable. Please try again later."
            }
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }
    void SaveSession(HubSession? value)
    {
        if (sessionPath != null)
        {
            if (value == null || !rememberLogin) { if (File.Exists(sessionPath)) File.Delete(sessionPath); }
            else {
                Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
                var encrypted = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(new HubRememberedSession(value, rememberUntil)), Encoding.UTF8.GetBytes(origin.AbsoluteUri), DataProtectionScope.CurrentUser);
                string temp = sessionPath + ".tmp";
                try { File.WriteAllBytes(temp, encrypted); File.Move(temp, sessionPath, true); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
        if (Session?.UserId != value?.UserId || value == null) IsOwner = false;
        Session = value; AccountChanged?.Invoke();
    }
    static HubSession? ParseSession(JsonElement root)
    {
        if (!root.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String) return null;
        var user = root.GetProperty("user");
        string display = "Soundshark user";
        if (user.TryGetProperty("user_metadata", out var metadata) && metadata.TryGetProperty("display_name", out var name)) display = name.GetString() ?? display;
        return new(access.GetString()!, root.GetProperty("refresh_token").GetString()!, user.GetProperty("id").GetGuid(), display,
            root.TryGetProperty("expires_at", out var expires) ? expires.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeSeconds() + root.GetProperty("expires_in").GetInt64());
    }
    bool AcceptSession(JsonElement root)
    {
        var value = ParseSession(root); if (value == null) return false;
        if (rememberLogin && rememberUntil == 0) rememberUntil = now().AddDays(30).ToUnixTimeSeconds();
        SaveSession(value);
        return true;
    }
    public void RememberLogin(bool enabled)
    {
        rememberLogin = enabled; rememberUntil = 0;
        if (sessionPath != null && File.Exists(sessionPath)) File.Delete(sessionPath);
    }
    public async Task<bool> Resume(CancellationToken ct)
    {
        if (Session == null) return false;
        try {
            await Refresh(ct);
            using var result = await Send(Request(HttpMethod.Get, "/auth/v1/user", true), ct);
            if (result.RootElement.GetProperty("id").GetGuid() != Session!.UserId) { SaveSession(null); return false; }
            return true;
        } catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { SaveSession(null); return false; }
        catch (InvalidOperationException) { return false; }
    }
    public Task EnsureAccess(CancellationToken ct) => Refresh(ct);
    async Task<bool> Auth(string path, object payload, CancellationToken ct)
    {
        await authGate.WaitAsync(ct);
        try { var request = Request(HttpMethod.Post, path); request.Content = JsonContent.Create(payload);
            using var result = await Send(request, ct); return AcceptSession(result.RootElement); }
        finally { authGate.Release(); }
    }
    public async Task SignIn(string identifier, string password, CancellationToken ct)
    {
        identifier = identifier.Trim();
        if (identifier.Length == 0 || password.Length == 0) throw new InvalidDataException("Enter your username or email and password.");
        bool accepted = identifier.Contains('@')
            ? await Auth("/auth/v1/token?grant_type=password", new { email = identifier, password }, ct)
            : await Auth("/functions/v1/sounds-hub/auth/login", new { identifier, password }, ct);
        if (!accepted) throw new InvalidDataException("Sign-in did not return a session.");
    }
    public Task<bool> SignUp(string email, string password, string displayName, CancellationToken ct)
    {
        displayName = displayName.Trim();
        email = email.Trim();
        if (email.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(email, out var address) || address.Address != email)
            throw new InvalidDataException("Enter a valid email address.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(displayName, @"\A[A-Za-z0-9_]{3,24}\z"))
            throw new InvalidDataException("Use a username of 3–24 letters, numbers or underscores.");
        if (password.Length is < 6 or > 12) throw new InvalidDataException("Use a password of 6–12 characters.");
        return Auth("/auth/v1/signup", new { email, password, data = new { username = displayName, display_name = displayName } }, ct);
    }
    public async Task Verify(string email, string code, bool recovery, CancellationToken ct)
    { if (!await Auth("/auth/v1/verify", new { email, token = code.Trim(), type = recovery ? "recovery" : "signup" }, ct)) throw new InvalidDataException("Verification did not return a session."); }
    async Task<JsonDocument> HubAuth(string route, object payload, CancellationToken ct)
    {
        var request = Request(HttpMethod.Post, "/functions/v1/sounds-hub/auth/" + route);
        request.Content = JsonContent.Create(payload); return await Send(request, ct);
    }
    public async Task<bool> AccountExists(string identifier, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(identifier)) return false;
        using var result = await HubAuth("check", new { identifier = identifier.Trim() }, ct);
        return result.RootElement.GetProperty("exists").GetBoolean();
    }
    public async Task<HubRecovery> BeginRecovery(string identifier, CancellationToken ct)
    {
        ClearRecovery();
        if (string.IsNullOrWhiteSpace(identifier)) throw new InvalidDataException("Enter your Soundshark username or email first.");
        using var result = await HubAuth("recover", new { identifier = identifier.Trim() }, ct);
        return new(result.RootElement.GetProperty("challengeId").GetGuid());
    }
    public async Task ResendRecovery(HubRecovery recovery, CancellationToken ct)
    { using var result = await HubAuth("resend", new { challengeId = recovery.ChallengeId }, ct); }
    public async Task<HubRecoveryResult> VerifyRecovery(HubRecovery recovery, string code, CancellationToken ct)
    {
        using var result = await HubAuth("verify", new { challengeId = recovery.ChallengeId, token = code.Trim() }, ct);
        var root = result.RootElement;
        if (!root.GetProperty("verified").GetBoolean()) return new(false, root.GetProperty("remaining").GetInt32());
        recoverySession = ParseSession(root.GetProperty("session")) ?? throw new InvalidDataException("Recovery verification did not return a session.");
        return new(true, 0);
    }
    public async Task SetRecoveryPassword(string password, CancellationToken ct)
    {
        if (password.Length is < 6 or > 12) throw new InvalidDataException("Use a password of 6–12 characters.");
        var session = recoverySession ?? throw new InvalidOperationException("Verify your recovery code first.");
        var request = Request(HttpMethod.Put, "/auth/v1/user"); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        request.Content = JsonContent.Create(new { password }); using var result = await Send(request, ct);
        await EndRecovery(ct);
    }
    public async Task EndRecovery(CancellationToken ct)
    {
        var session = recoverySession; recoverySession = null;
        if (session == null) return;
        try {
            var request = Request(HttpMethod.Post, "/auth/v1/logout?scope=local"); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            using var response = await http.SendAsync(request, ct);
        } catch (HttpRequestException) { }
    }
    public void ClearRecovery() => recoverySession = null;
    async Task Refresh(CancellationToken ct)
    {
        await authGate.WaitAsync(ct);
        try {
            if (Session == null) throw new InvalidOperationException("Sign in to Sounds Hub first.");
            if (rememberUntil > 0 && now().ToUnixTimeSeconds() >= rememberUntil) { SaveSession(null); throw new InvalidOperationException("Your 30-day login has expired. Please sign in again."); }
            if (Session.ExpiresAt > now().ToUnixTimeSeconds() + 60) return;
            var request = Request(HttpMethod.Post, "/auth/v1/token?grant_type=refresh_token"); request.Content = JsonContent.Create(new { refresh_token = Session.RefreshToken });
            using var result = await Send(request, ct);
            if (!AcceptSession(result.RootElement)) throw new InvalidDataException("Please sign in again.");
        } catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized) {
            SaveSession(null); throw;
        } finally { authGate.Release(); }
    }
    public async Task ChangePassword(string password, CancellationToken ct)
    {
        if (password.Length is < 6 or > 12) throw new InvalidDataException("Use a password of 6–12 characters.");
        await Refresh(ct); var request = Request(HttpMethod.Put, "/auth/v1/user", true); request.Content = JsonContent.Create(new { password }); using var result = await Send(request, ct);
    }
    public async Task SignOut(CancellationToken ct)
    {
        try { if (Session != null) { using var request = Request(HttpMethod.Post, "/auth/v1/logout?scope=local", true); using var response = await http.SendAsync(request, ct); } }
        finally { SaveSession(null); }
    }
    public async Task<HubCountryPreference> CountryPreference(CancellationToken ct)
    {
        await Refresh(ct);
        try {
            using var result = await Send(Request(HttpMethod.Get, "/functions/v1/sounds-hub/profile", true), ct);
            var value = result.RootElement.Deserialize<HubCountryPreference>(Json) ?? new(null, false); value.Validate(); return value;
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
        {
            // Older Hub deployments do not have the private profile endpoint yet. Let the
            // user open the picker, but keep display disabled until the server can save it.
            return new(HubCountries.Default.Code, false);
        }
    }
    public async Task SetCountryPreference(string countryCode, bool showCountry, CancellationToken ct)
    {
        countryCode = countryCode.Trim().ToUpperInvariant(); var value = new HubCountryPreference(countryCode, showCountry); value.Validate();
        await Refresh(ct); var request = Request(HttpMethod.Put, "/functions/v1/sounds-hub/profile", true); request.Content = JsonContent.Create(new { countryCode, showCountry });
        using var result = await Send(request, ct);
    }
    public async Task<HubPage> Browse(string search, int page, bool mine, CancellationToken ct)
    {
        if (page < 0) throw new ArgumentOutOfRangeException(nameof(page));
        if (mine) await Refresh(ct);
        var request = Request(HttpMethod.Get, "/functions/v1/sounds-hub/sounds?q=" + Uri.EscapeDataString(search.Trim()[..Math.Min(search.Trim().Length, 80)]) + "&page=" + page + (mine ? "&mine=1" : ""), mine);
        using var result = await Send(request, ct);
        var value = result.RootElement.Deserialize<HubPage>(Json) ?? throw new InvalidDataException("Sounds Hub returned no catalogue.");
        if (value.Sounds == null || value.Sounds.Length > 40) throw new InvalidDataException("Sounds Hub returned an oversized catalogue.");
        foreach (var sound in value.Sounds) { if (sound == null) throw new InvalidDataException("Sounds Hub returned invalid sound details."); sound.Validate(); } return value;
    }
    public async Task<byte[]> Download(HubSound sound, CancellationToken ct)
    {
        sound.Validate();
        using var request = Request(HttpMethod.Get, "/functions/v1/sounds-hub/sounds/" + sound.Id + "/audio");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        if (response.Content.Headers.ContentLength is long length && length != sound.Bytes) throw new InvalidDataException("The sound changed. Refresh Sounds Hub before downloading it again.");
        var bytes = await ReadBounded(await response.Content.ReadAsStreamAsync(ct), (int)sound.Bytes, ct);
        if (bytes.Length != sound.Bytes || !Convert.ToHexString(SHA256.HashData(bytes)).Equals(sound.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The sound failed its download integrity check.");
        return bytes;
    }
    public async Task<HubSound> Upload(string title, byte[] mp3, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(title) || title.Trim().Length > 80 || mp3.Length is <= 0 or > MaxAudioBytes) throw new InvalidDataException("Use a title up to 80 characters and audio up to 10 MB.");
        await Refresh(ct);
        var request = Request(HttpMethod.Post, "/functions/v1/sounds-hub/upload", true);
        var form = new MultipartFormDataContent(); form.Add(new StringContent(title.Trim()), "title"); form.Add(new StringContent("confirmed"), "rights");
        var audio = new ByteArrayContent(mp3); audio.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg"); form.Add(audio, "file", "sound.mp3"); request.Content = form;
        using var result = await Send(request, ct); var sound = result.RootElement.Deserialize<HubSound>(Json) ?? throw new InvalidDataException("Upload did not return a sound."); sound.Validate(); return sound;
    }
    public async Task Delete(HubSound sound, CancellationToken ct)
    {
        await Refresh(ct);
        if (Session?.UserId != sound.OwnerId) {
            await RefreshRole(ct);
            if (!IsOwner) throw new InvalidOperationException("Only the uploader or Soundshark Owner can remove this sound.");
        }
        using var result = await Send(Request(HttpMethod.Delete, "/functions/v1/sounds-hub/sounds/" + sound.Id, true), ct);
    }
    public async Task RemoveReportedSound(Guid id, CancellationToken ct)
    {
        await RefreshRole(ct);
        if (!IsOwner) throw new InvalidOperationException("Owner access is required.");
        using var result = await Send(Request(HttpMethod.Delete,"/functions/v1/sounds-hub/sounds/"+id,true),ct);
    }
    public async Task Report(HubSound sound, CancellationToken ct)
    { await Refresh(ct); using var result = await Send(Request(HttpMethod.Post, "/functions/v1/sounds-hub/sounds/" + sound.Id + "/report", true), ct); }
    public void Dispose() { http.Dispose(); }
}
