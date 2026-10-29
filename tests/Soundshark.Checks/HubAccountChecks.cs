using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

using Soundshark;

internal static partial class Program
{
    static async Task HubAccountChecks(Window owner)
    {
        var source = new SoundsHubSource("https://account-test.supabase.co/", "sb_publishable_test");
        var clock = DateTimeOffset.UtcNow;
        var user = Guid.NewGuid(); var challenge = new HubRecovery(Guid.NewGuid());
        bool revoked = false; int attempts = 0, passwordWrites = 0, usernameLogins = 0;
        object Session() => new { access_token = "test-access", refresh_token = "test-refresh", expires_at = clock.AddHours(1).ToUnixTimeSeconds(), user = new { id = user, user_metadata = new { display_name = "TestUser" } } };
        HubHandler Handler() => new(async (request, ct) => {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/auth/v1/user" && request.Method == HttpMethod.Get)
                return new(revoked ? HttpStatusCode.Unauthorized : HttpStatusCode.OK) { Content = JsonContent.Create(new { id = user }) };
            if (path.EndsWith("/auth/login")) { usernameLogins++; return new(HttpStatusCode.OK) { Content = JsonContent.Create(Session()) }; }
            if (path == "/auth/v1/token") return new(HttpStatusCode.OK) { Content = JsonContent.Create(Session()) };
            if (path.EndsWith("/auth/recover")) return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { challengeId = challenge.ChallengeId }) };
            if (path.EndsWith("/auth/verify")) {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                attempts++;
                bool correct = body.RootElement.GetProperty("token").GetString() == "12345678";
                return new(HttpStatusCode.OK) { Content = correct ? JsonContent.Create(new { verified = true, session = Session() }) : JsonContent.Create(new { verified = false, remaining = 3 - attempts }) };
            }
            if (path == "/auth/v1/user" && request.Method == HttpMethod.Put) passwordWrites++;
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { ok = true }) };
        });
        string path = Path.Combine(Paths.Root, "account-remember-test.dat");
        using (var client = new SoundsHubClient(source, Handler(), path, () => clock)) {
            await client.SignIn("TestUser", "123456", CancellationToken.None);
            Check(usernameLogins == 1 && !File.Exists(path), "username login uses server authentication and unchecked remember-me writes no session to disk");
            client.RememberLogin(true);
            await client.SignIn("user@example.test", "123456", CancellationToken.None);
            Check(File.Exists(path), "remember-me persists the encrypted session only after successful login");
        }
        clock = clock.AddDays(29);
        using (var client = new SoundsHubClient(source, Handler(), path, () => clock))
            Check(await client.Resume(CancellationToken.None), "remembered login resumes and refreshes successfully before day 30");
        clock = clock.AddDays(1);
        using (var client = new SoundsHubClient(source, Handler(), path, () => clock))
            Check(!await client.Resume(CancellationToken.None) && !File.Exists(path), "day-30 expiry clears the remembered login even after a day-29 refresh");
        using (var client = new SoundsHubClient(source, Handler(), path, () => clock)) {
            client.RememberLogin(true); await client.SignIn("user@example.test", "123456", CancellationToken.None);
            clock = clock.AddDays(30);
            Check(!await client.Resume(CancellationToken.None) && client.Session == null, "a running client also expires at the fixed 30-day deadline");
        }
        using (var client = new SoundsHubClient(source, Handler(), path, () => clock)) {
            client.RememberLogin(true); await client.SignIn("user@example.test", "123456", CancellationToken.None);
            revoked = true;
            Check(!await client.Resume(CancellationToken.None) && !File.Exists(path), "revoked remembered sessions are rejected and removed before opening the Hub");
            revoked = false;
        }
        using (var client = new SoundsHubClient(source, Handler(), path, () => clock)) {
            var recovery = await client.BeginRecovery("TestUser", CancellationToken.None);
            Check((await client.VerifyRecovery(recovery, "12345678", CancellationToken.None)).Verified && client.Session == null && !File.Exists(path), "verified recovery tokens do not grant Hub access or create a remembered login");
            await client.SetRecoveryPassword("123456", CancellationToken.None);
            Check(passwordWrites == 1 && client.Session == null, "password reset updates the account and returns to login without retaining its temporary session");
            attempts = 0;
            var recoveryWindow = new HubRecoveryWindow(owner, client, challenge) { ShowActivated = false };
            recoveryWindow.Show(); recoveryWindow.UpdateLayout();
            var resend = Descendants<Button>(recoveryWindow).Single(b => b.Content is string s && s.StartsWith("Send Again"));
            Check(!resend.IsEnabled, "recovery resend starts with a 60-second cooldown");
            var code = Descendants<TextBox>(recoveryWindow).Single(t => t.Name == "HubRecoveryCode");
            var verify = Descendants<Button>(recoveryWindow).Single(b => Equals(b.Content, "Verify Code"));
            for (int i = 0; i < 3; i++) { code.Text = "00000000"; verify.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await recoveryWindow.Pending; }
            Check(!recoveryWindow.IsVisible && recoveryWindow.RestartMessage.Contains("Three"), "three invalid recovery codes close the token window and require restarting recovery");
            attempts = 0;
            recoveryWindow = new HubRecoveryWindow(owner, client, challenge) { ShowActivated = false };
            recoveryWindow.Show(); recoveryWindow.UpdateLayout();
            code = Descendants<TextBox>(recoveryWindow).Single(t => t.Name == "HubRecoveryCode"); code.Text = "12345678";
            Descendants<Button>(recoveryWindow).Single(b => Equals(b.Content, "Verify Code")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await recoveryWindow.Pending;
            var password = Descendants<PasswordBox>(recoveryWindow).Single(p => p.Name == "HubNewPassword");
            var confirm = Descendants<PasswordBox>(recoveryWindow).Single(p => p.Name == "HubConfirmNewPassword");
            var set = Descendants<Button>(recoveryWindow).Single(b => Equals(b.Content, "Set New Password"));
            password.Password = "123456"; confirm.Password = "654321";
            Check(password.IsVisible && confirm.IsVisible && !code.IsVisible && !set.IsEnabled && set.Effect == null, "valid recovery reveals both password fields and mismatches cannot submit");
            confirm.Password = password.Password;
            Check(set.IsEnabled && set.Effect is System.Windows.Media.Effects.DropShadowEffect, "matching valid passwords enable Set New Password and give it a green glow");
            Snapshot(recoveryWindow, "hub-new-password-match.png");
            set.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await recoveryWindow.Pending;
            Check(recoveryWindow.Completed && passwordWrites == 2 && client.Session == null, "matching password form completes reset and closes without exposing the Hub");
        }
    }
}
