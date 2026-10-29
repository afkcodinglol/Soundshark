using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using System.Windows.Input;

namespace Soundshark;

public static class HubLoginFlow
{
    public static async Task Open(MainWindow owner)
    {
        var client = owner.Hub;
        if (!await client.Resume(CancellationToken.None) && !await Authenticate(owner, client)) return;
        await owner.RefreshHubRole();
        var hub = new SoundsHubWindow(owner, client); hub.ShowDialog(); await hub.Pending;
    }
    public static async Task<bool> Authenticate(Window owner, SoundsHubClient client)
    {
        string notice = "";
        bool noticeIsError = false;
        while (client.Session == null) {
            var login = new HubAccountWindow(owner, client, notice, noticeIsError); login.ShowDialog(); await login.Pending;
            if (client.Session != null) return true;
            if (login.RecoveryRequested) {
                var recovery = new HubRecoveryWindow(owner, client); recovery.ShowDialog(); await recovery.Pending; await recovery.AccountCheck;
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await client.EndRecovery(cleanup.Token); } catch (OperationCanceledException) { }
                notice = recovery.Completed ? "Password updated. Sign in with your new password." : recovery.RestartMessage;
                noticeIsError = !recovery.Completed;
                continue;
            }
            if (login.CreateAccountRequested) {
                var signup = new HubSignupWindow(owner, client); signup.ShowDialog(); await signup.Pending;
                if (client.Session == null && signup.VerificationEmail is { } email) {
                    var verification = new HubSignupVerificationWindow(owner, client, email); verification.ShowDialog(); await verification.Pending;
                    notice = verification.RestartMessage; noticeIsError = true;
                }
                continue;
            }
            return false;
        }
        return true;
    }
}

public abstract class HubAuthWindow : Window
{
    protected readonly CancellationTokenSource Lifetime = new();
    protected readonly StackPanel Panel = new() { Margin = new Thickness(28) };
    protected readonly TextBlock Message = Ui.Text("", 13, true);
    protected readonly List<Button> Buttons = [];
    protected bool Busy, ClosedWindow;
    readonly Func<bool> capsLockState;
    public Task Pending { get; private set; } = Task.CompletedTask;
    protected HubAuthWindow(Window owner, string title, Func<bool>? capsLockState = null)
    {
        Owner = owner; Title = title; Width = 520; Height = 540; MinWidth = 460; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(StyleProperty, typeof(Window));
        this.capsLockState = capsLockState ?? (() => Keyboard.IsKeyToggled(Key.CapsLock));
        Content = new ScrollViewer { Content = Panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Panel.Children.Add(Ui.Text("Sounds Hub", 28));
        Panel.Children.Add(Ui.Text(title, 16, true));
        Message.Name = "HubAccountMessage"; Message.Margin = new Thickness(0, 12, 0, 0);
        Closing += (_, _) => { ClosedWindow = true; Lifetime.Cancel(); };
    }
    protected void Error(string text) { Message.Foreground = new SolidColorBrush(Color.FromRgb(255, 113, 125)); Message.Text = text; }
    protected void Notice(string text) { Message.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); Message.Text = text; }
    protected void WatchCapsLock(params PasswordBox[] fields) => WatchCapsLock(Panel, fields);
    protected void WatchCapsLock(StackPanel destination, params PasswordBox[] fields)
    {
        var warning = Ui.Text("Caps Lock is on.", 12);
        warning.Name = "HubCapsLockWarning"; warning.Foreground = new SolidColorBrush(Color.FromRgb(255, 190, 100));
        warning.Margin = new Thickness(0, 6, 0, 0); warning.Visibility = Visibility.Collapsed;
        destination.Children.Add(warning);
        void Refresh() => warning.Visibility = fields.Any(f => f.IsKeyboardFocusWithin) && capsLockState() ? Visibility.Visible : Visibility.Collapsed;
        foreach (var field in fields) { field.GotKeyboardFocus += (_, _) => Refresh(); field.LostKeyboardFocus += (_, _) => Refresh(); }
        ProcessInputEventHandler input = (_, _) => { if (IsActive) Refresh(); };
        InputManager.Current.PostProcessInput += input;
        Activated += (_, _) => Refresh(); Loaded += (_, _) => Refresh();
        Closed += (_, _) => InputManager.Current.PostProcessInput -= input;
    }
    protected void Field(string label, Control field)
    {
        var heading = Ui.Text(label, 13, true); heading.Margin = new Thickness(0, 16, 0, 6);
        Panel.Children.Add(heading); Panel.Children.Add(field);
        System.Windows.Automation.AutomationProperties.SetName(field, label);
    }
    protected static PasswordBox Password(string name)
    {
        var field = new PasswordBox { Name = name, MaxLength = 128, Padding = new Thickness(10, 8, 10, 8) };
        field.SetResourceReference(BackgroundProperty, "Surface"); field.SetResourceReference(ForegroundProperty, "Text"); return field;
    }
    protected Button Action(string label, Func<Task> action)
    {
        var button = Ui.Button(label, () => { if (!Busy) Pending = Run(action); }); Buttons.Add(button); return button;
    }
    async Task Run(Func<Task> action)
    {
        if (Busy || ClosedWindow) return;
        Busy = true; UpdateButtons(); Notice("");
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!ClosedWindow) Error(ex.Message); }
        finally { Busy = false; if (!ClosedWindow) UpdateButtons(); }
    }
    protected virtual void UpdateButtons() { foreach (var b in Buttons) b.IsEnabled = !Busy; }
}

public sealed class HubAccountWindow : HubAuthWindow
{
    public bool RecoveryRequested { get; private set; }
    public bool CreateAccountRequested { get; private set; }
    public HubAccountWindow(Window owner, SoundsHubClient client, string notice = "", bool noticeIsError = false, Func<bool>? capsLockState = null) : base(owner, "Sign in to continue", capsLockState)
    {
        var identifier = new TextBox { Name = "HubIdentifier", MaxLength = 254 };
        var password = Password("HubPassword");
        var remember = new CheckBox { Name = "HubRememberMe", Content = "Remember me for 30 days", Margin = new Thickness(0, 18, 0, 18) };
        Field("Username or email", identifier); Field("Password", password); WatchCapsLock(password); Panel.Children.Add(remember);
        var signIn = Action("Sign In", async () => {
            client.RememberLogin(remember.IsChecked == true);
            try { await client.SignIn(identifier.Text, password.Password, Lifetime.Token); if (!ClosedWindow) Close(); }
            finally { password.Clear(); }
        }); signIn.IsDefault = true;
        var create = Action("Create Account", () => { CreateAccountRequested = true; Close(); return Task.CompletedTask; });
        var forgot = Action("Forgot Password", () => {
            RecoveryRequested = true; Close(); return Task.CompletedTask;
        });
        var actions = new Grid();
        for (int column = 0; column < 5; column++) actions.ColumnDefinitions.Add(new() { Width = column % 2 == 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(10) });
        var accountButtons = new[] { signIn, create, forgot };
        for (int index = 0; index < accountButtons.Length; index++) {
            var button = accountButtons[index]; button.Margin = new Thickness(0); button.Padding = new Thickness(8, 10, 8, 10);
            button.MinHeight = 42; button.HorizontalAlignment = HorizontalAlignment.Stretch; button.VerticalAlignment = VerticalAlignment.Stretch;
            button.SetResourceReference(FontSizeProperty, "Font13"); Grid.SetColumn(button, index * 2); actions.Children.Add(button);
        }
        Panel.Children.Add(actions);
        if (noticeIsError) Error(notice); else Notice(notice); Panel.Children.Add(Message);
        Loaded += (_, _) => identifier.Focus();
        Closing += (_, _) => password.Clear();
    }
}

public sealed class HubRecoveryWindow : HubAuthWindow
{
    readonly TextBox identifier = new() { Name = "HubRecoveryIdentifier", MaxLength = 254 };
    readonly TextBox code = new() { Name = "HubRecoveryCode", MaxLength = 10 };
    readonly PasswordBox password = Password("HubNewPassword"), confirm = Password("HubConfirmNewPassword");
    readonly Button resend, verify, setPassword, sendCode;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly Stopwatch cooldown = Stopwatch.StartNew();
    bool verified;
    int attempts;
    HubRecovery? activeChallenge;
    CancellationTokenSource? accountCheckCancellation;
    string? validatedIdentifier;
    bool accountFound;
    public Task AccountCheck { get; private set; } = Task.CompletedTask;
    public bool Completed { get; private set; }
    public string RestartMessage { get; private set; } = "Recovery cancelled. You can start again below.";
    public HubRecoveryWindow(Window owner, SoundsHubClient client, HubRecovery? challenge = null, Func<bool>? capsLockState = null) : base(owner, "Recover your account", capsLockState)
    {
        activeChallenge = challenge;
        var identityPanel = new StackPanel { Visibility = challenge == null ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(0, 20, 0, 0) };
        identityPanel.Children.Add(Ui.Text("Username or email", 13, true)); identityPanel.Children.Add(identifier);
        System.Windows.Automation.AutomationProperties.SetName(identifier, "Recovery username or email");
        var tokenPanel = new StackPanel { Visibility = challenge == null ? Visibility.Collapsed : Visibility.Visible };
        sendCode = Action("Send Recovery Code", async () => {
            if (!accountFound || validatedIdentifier != identifier.Text.Trim()) return;
            activeChallenge = await client.BeginRecovery(validatedIdentifier, Lifetime.Token);
            if (ClosedWindow) return;
            identityPanel.Visibility = Visibility.Collapsed; tokenPanel.Visibility = Visibility.Visible;
            cooldown.Restart(); Notice("A recovery code was sent to your account’s email address."); code.Focus();
        });
        sendCode.Margin = new Thickness(0, 16, 0, 0); identityPanel.Children.Add(sendCode); Panel.Children.Add(identityPanel);
        identifier.TextChanged += (_, _) => {
            accountCheckCancellation?.Cancel(); accountCheckCancellation?.Dispose();
            accountCheckCancellation = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
            accountFound = false; validatedIdentifier = null; Notice(""); UpdateButtons();
            AccountCheck = CheckAccount(identifier.Text.Trim(), accountCheckCancellation.Token);
        };
        async Task CheckAccount(string value, CancellationToken ct)
        {
            if (value.Length == 0) return;
            try {
                await Task.Delay(350, ct);
                bool exists = await client.AccountExists(value, ct);
                if (ClosedWindow || ct.IsCancellationRequested || identifier.Text.Trim() != value) return;
                accountFound = exists; validatedIdentifier = exists ? value : null;
                if (exists) Notice("Account found. Send a recovery code to continue."); else Error("No account matches that username or email.");
            } catch (OperationCanceledException) { }
            catch (Exception ex) { if (!ClosedWindow && !ct.IsCancellationRequested) Error(ex.Message); }
            finally { if (!ClosedWindow && !ct.IsCancellationRequested) UpdateButtons(); }
        }
        tokenPanel.Children.Add(Ui.Text("Enter the code sent to your account’s email address.", 14, true));
        tokenPanel.Children.Add(code);
        System.Windows.Automation.AutomationProperties.SetName(code, "Recovery code");
        tokenPanel.Children.Add(Ui.Text("Can’t find it? Check your spam or junk folder.", 12, true));
        verify = Action("Verify Code", async () => {
            if (string.IsNullOrWhiteSpace(code.Text)) { Error("Enter the code from your email."); return; }
            HubRecoveryResult result;
            try { result = await client.VerifyRecovery(activeChallenge!, code.Text, Lifetime.Token); }
            catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Gone) {
                RestartMessage = "This recovery attempt ended. Enter your username or email and start again."; Close(); return;
            }
            if (!result.Verified) {
                attempts = 3 - result.Remaining; code.Clear();
                if (attempts >= 3) { RestartMessage = "Three incorrect codes. Start password recovery again."; Close(); }
                else Error($"Incorrect code. {3 - attempts} attempt(s) remaining.");
                return;
            }
            verified = true; code.Clear(); tokenPanel.Visibility = Visibility.Collapsed;
            passwordPanel!.Visibility = Visibility.Visible; Notice("Code verified. Choose your new password."); password.Focus();
        });
        resend = Action("Send Again", async () => {
            if (cooldown.Elapsed.TotalSeconds < 60) return;
            await client.ResendRecovery(activeChallenge!, Lifetime.Token); cooldown.Restart(); Notice("A new code was sent. Check your inbox and spam folder.");
        });
        tokenPanel.Children.Add(Ui.Row(verify, resend)); tokenPanel.Margin = new Thickness(0, 20, 0, 0); Panel.Children.Add(tokenPanel);
        passwordPanel = new StackPanel { Visibility = Visibility.Collapsed };
        var newLabel = Ui.Text("New Password (6–12 characters)", 13, true); newLabel.Margin = new Thickness(0, 16, 0, 6);
        var confirmLabel = Ui.Text("Confirm New Password", 13, true); confirmLabel.Margin = new Thickness(0, 16, 0, 6);
        passwordPanel.Children.Add(newLabel); passwordPanel.Children.Add(password);
        passwordPanel.Children.Add(confirmLabel); passwordPanel.Children.Add(confirm);
        WatchCapsLock(passwordPanel, password, confirm);
        System.Windows.Automation.AutomationProperties.SetName(password, "New Password"); System.Windows.Automation.AutomationProperties.SetName(confirm, "Confirm New Password");
        setPassword = Action("Set New Password", async () => {
            if (!PasswordsMatch) return;
            await client.SetRecoveryPassword(password.Password, Lifetime.Token); Completed = true; Close();
        });
        setPassword.Margin = new Thickness(0, 18, 0, 0); passwordPanel.Children.Add(setPassword); Panel.Children.Add(passwordPanel); Panel.Children.Add(Message);
        password.PasswordChanged += (_, _) => UpdateButtons(); confirm.PasswordChanged += (_, _) => UpdateButtons();
        timer.Tick += (_, _) => UpdateButtons(); timer.Start();
        Closing += (_, _) => { timer.Stop(); accountCheckCancellation?.Cancel(); password.Clear(); confirm.Clear(); code.Clear(); };
        Closed += (_, _) => accountCheckCancellation?.Dispose();
        UpdateButtons(); Loaded += (_, _) => { if (activeChallenge == null) identifier.Focus(); else code.Focus(); };
    }
    readonly StackPanel passwordPanel;
    bool PasswordsMatch => password.Password.Length is >= 6 and <= 12 && password.Password == confirm.Password;
    protected override void UpdateButtons()
    {
        base.UpdateButtons();
        if (resend == null || setPassword == null) return;
        identifier.IsEnabled = !Busy && activeChallenge == null;
        bool matched = accountFound && validatedIdentifier == identifier.Text.Trim();
        sendCode.IsEnabled = !Busy && activeChallenge == null && matched;
        SetGreen(sendCode, matched);
        int remaining = Math.Max(0, 60 - (int)cooldown.Elapsed.TotalSeconds);
        resend.Content = remaining > 0 ? $"Send Again ({remaining}s)" : "Send Again";
        resend.IsEnabled = !Busy && activeChallenge != null && !verified && remaining == 0; verify.IsEnabled = !Busy && activeChallenge != null && !verified;
        bool ready = verified && PasswordsMatch;
        setPassword.IsEnabled = !Busy && ready;
        SetGreen(setPassword, ready);
    }
    static void SetGreen(Button button, bool ready)
    {
        if (ready) {
            if (button.Effect != null) return;
            button.Background = new SolidColorBrush(Color.FromRgb(26, 118, 61)); button.Foreground = Brushes.White;
            button.Effect = new DropShadowEffect { Color = Color.FromRgb(57, 255, 100), BlurRadius = 18, ShadowDepth = 0, Opacity = .8 };
        } else { button.ClearValue(BackgroundProperty); button.ClearValue(ForegroundProperty); button.Effect = null; }
    }
}

public sealed class HubSignupWindow : HubAuthWindow
{
    public string? VerificationEmail { get; private set; }
    public HubSignupWindow(Window owner, SoundsHubClient client, Func<bool>? capsLockState = null) : base(owner, "Create your account", capsLockState)
    {
        Height = 650;
        var email = new TextBox { Name = "HubEmail", MaxLength = 254 }; var username = new TextBox { Name = "HubUsername", MaxLength = 24 };
        var password = Password("HubSignupPassword"); var remember = new CheckBox { Content = "Remember me for 30 days", Margin = new Thickness(0, 16, 0, 16) };
        Field("Email", email); Field("Username (3–24 letters, numbers or underscores)", username); Field("Password (6–12 characters)", password);
        WatchCapsLock(password); Panel.Children.Add(remember);
        Panel.Children.Add(Action("Create Account", async () => {
            client.RememberLogin(remember.IsChecked == true);
            if (!await client.SignUp(email.Text.Trim(), password.Password, username.Text, Lifetime.Token)) VerificationEmail = email.Text.Trim();
            Close();
        }));
        Panel.Children.Add(Message);
        Closing += (_, _) => password.Clear();
    }
}

public sealed class HubSignupVerificationWindow : HubAuthWindow
{
    int attempts;
    public string RestartMessage { get; private set; } = "Verification cancelled. Choose Create Account to restart the sign-up process.";
    public HubSignupVerificationWindow(Window owner, SoundsHubClient client, string email) : base(owner, "Verify your email")
    {
        Height = 420; MinHeight = 360;
        var code = new TextBox { Name = "HubVerificationCode", MaxLength = 10 };
        var instructions = Ui.Text("Enter the verification code sent to " + email + ".", 14, true); instructions.Margin = new Thickness(0, 18, 0, 0);
        Panel.Children.Add(instructions); Field("Verification code", code);
        Panel.Children.Add(Ui.Text("Can’t find it? Check your spam or junk folder.", 12, true));
        void InvalidCode()
        {
            attempts++; code.Clear();
            if (attempts >= 3) { RestartMessage = "Three incorrect codes. Choose Create Account to restart the sign-up process."; Close(); }
            else Error($"Incorrect or expired code. {3 - attempts} attempt(s) remaining.");
        }
        var verify = Action("Verify Code", async () => {
            if (!System.Text.RegularExpressions.Regex.IsMatch(code.Text.Trim(), @"\A[0-9]{6,10}\z")) { InvalidCode(); return; }
            try { await client.Verify(email, code.Text, false, Lifetime.Token); Close(); }
            catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.UnprocessableEntity) { InvalidCode(); }
        }); verify.IsDefault = true;
        Panel.Children.Add(verify); Panel.Children.Add(Message);
        Loaded += (_, _) => code.Focus(); Closing += (_, _) => code.Clear();
    }
}
