using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using Soundshark;

internal static partial class Program
{
    static bool RedMessage(Window window) => Descendants<TextBlock>(window).Single(t => t.Name == "HubAccountMessage").Foreground is SolidColorBrush brush && brush.Color.R > 200 && brush.Color.G < 150;
    static async Task HubAccountPolishChecks(Window owner)
    {
        var source = new SoundsHubSource("https://account-polish.supabase.co/", "sb_publishable_test");
        var user = Guid.NewGuid(); var challenge = new HubRecovery(Guid.NewGuid());
        int signupRequests = 0, emails = 0; bool failVerificationNetwork = false;
        object Session() => new { access_token = "test-access", refresh_token = "test-refresh", expires_at = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), user = new { id = user, user_metadata = new { display_name = "TestUser" } } };
        var staleStarted = new TaskCompletionSource(); var staleRelease = new TaskCompletionSource();
        var handler = new HubHandler(async (request, ct) => {
            string path = request.RequestUri!.AbsolutePath;
            using var body = request.Content == null ? JsonDocument.Parse("{}") : JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct));
            if (path.EndsWith("/auth/check")) {
                string? value = body.RootElement.GetProperty("identifier").GetString();
                if (value == "slow-user") { staleStarted.TrySetResult(); await staleRelease.Task; }
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { exists = value is "TestUser" or "user@example.test" or "slow-user" }) };
            }
            if (path.EndsWith("/auth/recover")) { emails++; return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { challengeId = challenge.ChallengeId }) }; }
            if (path.EndsWith("/auth/verify")) return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { verified = true, session = Session() }) };
            if (path == "/auth/v1/signup") { signupRequests++; return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { user = new { id = user } }) }; }
            if (path == "/auth/v1/verify") {
                if (failVerificationNetwork) { failVerificationNetwork = false; return new(HttpStatusCode.ServiceUnavailable) { Content = JsonContent.Create(new { error_code = "unexpected_failure" }) }; }
                return body.RootElement.GetProperty("token").GetString() == "12345678"
                    ? new(HttpStatusCode.OK) { Content = JsonContent.Create(Session()) }
                    : new(HttpStatusCode.UnprocessableEntity) { Content = JsonContent.Create(new { error_code = "otp_expired", msg = "private upstream details" }) };
            }
            if (path == "/auth/v1/user" && body.RootElement.GetProperty("password").GetString() == "123456")
                return new(HttpStatusCode.UnprocessableEntity) { Content = JsonContent.Create(new { error_code = "same_password", msg = "private upstream details" }) };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { ok = true }) };
        });
        using var client = new SoundsHubClient(source, handler);
        bool capsLock = true;
        var login = new HubAccountWindow(owner, client, capsLockState: () => capsLock);
        login.Show(); login.Activate(); login.UpdateLayout();
        var loginPassword = Descendants<PasswordBox>(login).Single(); loginPassword.Focus();
        Check(Descendants<TextBlock>(login).Single(t => t.Name == "HubCapsLockWarning").IsVisible, "login shows a Caps Lock warning while its password field is focused");
        capsLock = false; Descendants<TextBox>(login).Single().Focus(); loginPassword.Focus();
        Check(!Descendants<TextBlock>(login).Single(t => t.Name == "HubCapsLockWarning").IsVisible, "Caps Lock warning disappears when Caps Lock is off");
        var create = Descendants<Button>(login).Single(b => Equals(b.Content, "Create Account"));
        var forgot = Descendants<Button>(login).Single(b => Equals(b.Content, "Forgot Password"));
        Check(ReferenceEquals(create.Parent, forgot.Parent) && Math.Abs(forgot.ActualHeight-create.ActualHeight)<1, "Forgot Password and Create Account have matching height in the same row");
        var primary = Descendants<Button>(login).Single(b => Equals(b.Content, "Sign In"));
        Check(ReferenceEquals(primary.Parent, create.Parent) && Math.Abs(primary.ActualWidth-create.ActualWidth)<1 && Math.Abs(create.ActualWidth-forgot.ActualWidth)<1 && Math.Abs(primary.ActualHeight-create.ActualHeight)<1, "all three login actions have identical dimensions and aligned columns");
        Snapshot(login, "hub-login-buttons.png");
        forgot.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await login.Pending;
        Check(login.RecoveryRequested && !login.IsVisible && emails == 0, "Forgot Password works with an empty login field and opens recovery without sending mail yet");

        var recovery = new HubRecoveryWindow(owner, client, capsLockState: () => true);
        recovery.Show(); recovery.Activate(); recovery.UpdateLayout();
        var identifier = Descendants<TextBox>(recovery).Single(t => t.Name == "HubRecoveryIdentifier");
        var send = Descendants<Button>(recovery).Single(b => Equals(b.Content, "Send Recovery Code"));
        Check(!send.IsEnabled, "recovery requires an account match before sending a code");
        identifier.Text = "missing"; await recovery.AccountCheck;
        Check(!send.IsEnabled && RedMessage(recovery) && emails == 0, "unknown recovery identities show a red warning without sending mail");
        identifier.Text = "slow-user"; var stale = recovery.AccountCheck; await staleStarted.Task;
        identifier.Text = "missing-again"; await recovery.AccountCheck; staleRelease.SetResult(); await stale;
        Check(!send.IsEnabled, "an out-of-date account match cannot enable recovery for a changed identity");
        identifier.Text = "TestUser"; await recovery.AccountCheck;
        Check(send.IsEnabled && send.Effect != null && emails == 0, "a confirmed account match lights Send Recovery Code green before any email is sent");
        Snapshot(recovery, "hub-recovery-account-found.png");
        send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await recovery.Pending;
        var code = Descendants<TextBox>(recovery).Single(t => t.Name == "HubRecoveryCode");
        Check(emails == 1 && !identifier.IsVisible && code.IsVisible, "sending recovery email changes the same window from identity entry to token entry");
        code.Text = "12345678";
        Descendants<Button>(recovery).Single(b => Equals(b.Content, "Verify Code")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await recovery.Pending;
        var password = Descendants<PasswordBox>(recovery).Single(p => p.Name == "HubNewPassword");
        var confirm = Descendants<PasswordBox>(recovery).Single(p => p.Name == "HubConfirmNewPassword");
        password.Focus();
        Check(Descendants<TextBlock>(recovery).Single(t => t.Name == "HubCapsLockWarning").IsVisible, "reset-password fields show the Caps Lock warning");
        password.Password = confirm.Password = "123456";
        var set = Descendants<Button>(recovery).Single(b => Equals(b.Content, "Set New Password"));
        set.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await recovery.Pending;
        var message = Descendants<TextBlock>(recovery).Single(t => t.Name == "HubAccountMessage");
        Check(recovery.IsVisible && RedMessage(recovery) && message.Text.Contains("current password") && !message.Text.Contains("private"), "same-password errors appear in red and keep the recovery form open without exposing server details");
        password.Password = confirm.Password = "654321";
        set.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await recovery.Pending;
        Check(recovery.Completed, "a rejected current password can be replaced without restarting token verification");

        var signup = new HubSignupWindow(owner, client, () => true);
        signup.Show(); signup.Activate(); signup.UpdateLayout();
        var email = Descendants<TextBox>(signup).Single(t => t.Name == "HubEmail");
        var username = Descendants<TextBox>(signup).Single(t => t.Name == "HubUsername");
        var signupPassword = Descendants<PasswordBox>(signup).Single();
        var submit = Descendants<Button>(signup).Single(b => Equals(b.Content, "Create Account"));
        email.Text = "invalid"; username.Text = "TestUser"; signupPassword.Password = "123456";
        submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await signup.Pending;
        Check(signup.IsVisible && RedMessage(signup) && signupRequests == 0 && signupPassword.Password == "123456", "invalid signup email stays on the form with a red warning and preserves the entered password");
        email.Text = "user@example.test"; username.Text = "bad name";
        submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await signup.Pending;
        Check(signup.IsVisible && RedMessage(signup) && signupRequests == 0, "invalid usernames cannot create accounts");
        username.Text = "TestUser"; signupPassword.Password = "12345";
        submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await signup.Pending;
        Check(signup.IsVisible && RedMessage(signup) && signupRequests == 0, "invalid signup password lengths cannot create accounts");
        signupPassword.Password = "123456"; signupPassword.Focus();
        Check(Descendants<TextBlock>(signup).Single(t => t.Name == "HubCapsLockWarning").IsVisible, "account creation displays its Caps Lock warning");
        Snapshot(signup, "hub-signup-validation.png");
        submit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await signup.Pending;
        Check(!signup.IsVisible && signup.VerificationEmail == "user@example.test" && signupRequests == 1, "valid signup closes the details window and hands off to separate email verification");
        var verification = new HubSignupVerificationWindow(owner, client, signup.VerificationEmail!);
        verification.Show(); verification.UpdateLayout();
        Check(Descendants<TextBox>(verification).Count() == 1 && !Descendants<PasswordBox>(verification).Any(), "signup verification is a separate window with only a token input");
        var signupCode = Descendants<TextBox>(verification).Single();
        var verify = Descendants<Button>(verification).Single(b => Equals(b.Content, "Verify Code"));
        signupCode.Text = "00000000"; verify.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await verification.Pending;
        Check(RedMessage(verification) && Descendants<TextBlock>(verification).Single(t => t.Name == "HubAccountMessage").Text.Contains("2 attempt"), "invalid signup codes show remaining attempts in red");
        failVerificationNetwork = true; signupCode.Text = "00000000"; verify.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await verification.Pending;
        signupCode.Text = "00000000"; verify.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await verification.Pending;
        Check(verification.IsVisible && Descendants<TextBlock>(verification).Single(t => t.Name == "HubAccountMessage").Text.Contains("1 attempt"), "network failures do not consume signup code attempts");
        signupCode.Text = "00000000"; verify.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await verification.Pending;
        Check(!verification.IsVisible && client.Session == null && verification.RestartMessage.Contains("Three"), "three rejected signup codes require restarting and do not sign in the user");
        verification = new HubSignupVerificationWindow(owner, client, "user@example.test");
        verification.Show(); verification.UpdateLayout();
        Descendants<TextBox>(verification).Single().Text = "12345678";
        Snapshot(verification, "hub-signup-token.png");
        Descendants<Button>(verification).Single(b => Equals(b.Content, "Verify Code")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await verification.Pending;
        Check(!verification.IsVisible && client.Session?.UserId == user, "a correct signup token signs the user in and closes verification");
    }
}
