using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Soundshark;

internal static partial class Program
{
    sealed class HubHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public readonly List<(string Path,string? Token)> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) {
            Requests.Add((request.RequestUri!.PathAndQuery,request.Headers.Authorization?.Parameter)); return respond(request,ct);
        }
    }
    static async Task HubChecks()
    {
        var source=new SoundsHubSource("https://soundshark-test.supabase.co/","sb_publishable_test");
        foreach(var invalid in new[]{source with{Url="http://example.test"},source with{Url="https://user@example.test"},source with{Url="https://example.test/path"},source with{PublishableKey="sb_secret_do-not-ship"},source with{PublishableKey="eyJservice-role"}}) {
            try { _=invalid.Origin; throw new Exception("Invalid Hub source accepted"); }
            catch(InvalidDataException) { Check(true,"Hub refuses insecure endpoints and privileged keys in desktop configuration"); }
        }
        string input=Path.Combine(Paths.Cache,"hub-fixture.mp3");
        await MediaTools.Run(Paths.Tool("ffmpeg"),["-nostdin","-v","error","-f","lavfi","-i","sine=frequency=440:duration=1","-ar","48000","-ac","2","-c:a","libmp3lame","-b:a","192k","-id3v2_version","0","-write_id3v1","0",input],CancellationToken.None);
        byte[] audio=await File.ReadAllBytesAsync(input); var info=await MediaTools.Probe(input,CancellationToken.None);
        var owner=Guid.NewGuid(); var sound=new HubSound(Guid.NewGuid(),"Community test sound","Test uploader",info.Duration,audio.Length,Convert.ToHexString(SHA256.HashData(audio)),owner);
        bool corrupt=false; int refreshes=0; bool rejectLogout=false;
        var handler=new HubHandler(async (request,ct)=>{
            string path=request.RequestUri!.AbsolutePath;
            if(path.EndsWith("/account")) return new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(new { isOwner=false })};
            if(path=="/auth/v1/signup") return new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(new{user=new{id=owner}})};
            if(path=="/auth/v1/token"||path=="/auth/v1/verify") {
                bool refresh=request.RequestUri.Query.Contains("refresh_token"); if(refresh)refreshes++;
                return new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(new{access_token="access-token",refresh_token="private-refresh-token",expires_at=DateTimeOffset.UtcNow.ToUnixTimeSeconds()+(refresh?3600:-10),user=new{id=owner,user_metadata=new{display_name="Test uploader"}}})};
            }
            if(path=="/auth/v1/logout"&&rejectLogout)throw new HttpRequestException("Simulated network interruption");
            if(path.EndsWith("/audio")) {var bytes=audio.ToArray();if(corrupt)bytes[^1]^=1;return new HttpResponseMessage(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};}
            if(path.EndsWith("/upload")) { _=await request.Content!.ReadAsByteArrayAsync(ct);return new HttpResponseMessage(HttpStatusCode.Created){Content=JsonContent.Create(sound)}; }
            if(path.EndsWith("/sounds"))return new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(new HubPage([sound],false))};
            return new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(new{ok=true})};
        });
        string sessionFile=Path.Combine(Paths.Root,"hub-session-test.dat");
        using var client=new SoundsHubClient(source,handler,sessionFile);
        client.RememberLogin(true);
        var page=await client.Browse("sound & test",0,false,CancellationToken.None);
        Check(page.Sounds.Single().Id==sound.Id&&handler.Requests.Last().Token==null,"community browsing works without signing in and does not send user credentials");
        Check(handler.Requests.Last().Path.Contains("%26"),"Hub search encodes user input into one query parameter");
        try{await client.Upload("test",audio,CancellationToken.None);throw new Exception("Anonymous upload accepted");}catch(InvalidOperationException){Check(true,"sharing requires an authenticated Hub account");}
        foreach (var invalidPassword in new[] { "12345", "1234567890123" }) {
            int requestsBefore = handler.Requests.Count;
            try { await client.SignUp("u@example.test",invalidPassword,"TestUploader",CancellationToken.None); throw new Exception("Out-of-range signup password accepted"); }
            catch (InvalidDataException) { }
            try { await client.ChangePassword(invalidPassword,CancellationToken.None); throw new Exception("Out-of-range reset password accepted"); }
            catch (InvalidDataException) { }
            Check(handler.Requests.Count == requestsBefore,"invalid new passwords are rejected before any authentication request");
        }
        foreach (var validPassword in new[] { "123456", "123456789012" })
            Check(!await client.SignUp("u@example.test",validPassword,"TestUploader",CancellationToken.None),"signup accepts both password boundaries and handles required email verification");
        await client.SignIn("u@example.test","a-long-test-password",CancellationToken.None);
        Check(client.Session?.UserId==owner&&File.Exists(sessionFile)&&!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(sessionFile)).Contains("private-refresh-token"),"remembered session is encrypted for the Windows user and never stores the password");
        await Task.WhenAll(client.Browse("",0,true,CancellationToken.None),client.Browse("",0,true,CancellationToken.None));
        Check(refreshes==1,"concurrent signed-in requests refresh an expired session only once");
        foreach (var validPassword in new[] { "123456", "123456789012" }) {
            await client.ChangePassword(validPassword,CancellationToken.None);
            Check(handler.Requests.Last().Path == "/auth/v1/user","password resets accept both length boundaries");
        }
        var downloaded=await client.Download(sound,CancellationToken.None);
        Check(downloaded.SequenceEqual(audio),"downloaded MP3 bytes match catalogue length and SHA-256");
        corrupt=true;
        try{await client.Download(sound,CancellationToken.None);throw new Exception("Corrupt audio accepted");}catch(InvalidDataException){Check(true,"changed audio is rejected before publication in Library");}
        corrupt=false;
        using(var tooLarge=new MemoryStream(new byte[33]))try{await SoundsHubClient.ReadBounded(tooLarge,32,CancellationToken.None);throw new Exception("Oversized response accepted");}catch(InvalidDataException){Check(true,"untrusted downloads have a streaming byte limit");}
        var shared=await client.Upload("Test sound",audio,CancellationToken.None);
        Check(shared.Id==sound.Id&&handler.Requests.Last().Token=="access-token","uploads use the signed-in user's token and return verified catalogue metadata");
        try{await client.Delete(sound with{OwnerId=Guid.NewGuid()},CancellationToken.None);throw new Exception("Another owner's delete accepted");}catch(InvalidOperationException){Check(true,"desktop prevents removing another uploader's sound");}
        string staged=Path.Combine(Paths.Cache,"hub-stage.mp3");await File.WriteAllBytesAsync(staged,audio);
        string first=HubImports.Publish(staged,"Duplicate Hub sound",Paths.Library);await File.WriteAllBytesAsync(staged,new byte[]{1,2,3});
        string second=HubImports.Publish(staged,"Duplicate Hub sound",Paths.Library);
        Check(first!=second&&(await File.ReadAllBytesAsync(first)).SequenceEqual(audio),"downloads choose a fresh Library filename without overwriting existing sounds");
        await File.WriteAllBytesAsync(staged,audio);string safe=HubImports.Publish(staged,"../../escape",Paths.Library);
        Check(Path.GetDirectoryName(Path.GetFullPath(safe))==Path.GetFullPath(Paths.Library),"server titles cannot escape the Library directory");
        var window=new MainWindow{ShowActivated=false};window.Show();await Task.Delay(100);window.UpdateLayout();
        Check(Descendants<Button>(window).Any(b=>b.Content is string s&&s=="Sounds Hub"),"Sounds Hub is a button in the main navigation");
        var opened=new TaskCompletionSource<bool>();
        _=window.Dispatcher.BeginInvoke(async()=>{
            await Task.Delay(80);
            var actual=Application.Current.Windows.OfType<HubAccountWindow>().FirstOrDefault(h=>h.Owner==window);
            opened.TrySetResult(actual!=null&&window.Hotkeys.Suspended&&!Application.Current.Windows.OfType<SoundsHubWindow>().Any());actual?.Close();
        });
        Descendants<Button>(window).Single(b=>b.Content is string s&&s=="Sounds Hub").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(await opened.Task.WaitAsync(TimeSpan.FromSeconds(5)),"main Sounds Hub button shows login before any Hub window and suspends global shortcuts");
        await Task.Delay(30);
        Check(!window.Hotkeys.Suspended,"closing Sounds Hub restores global shortcut handling");
        await HubAccountChecks(window);
        await HubAccountPolishChecks(window);
        await HubRefreshChecks(window, input);
        using var uiClient=new SoundsHubClient(source,new HubHandler((request,ct)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=request.RequestUri!.AbsolutePath=="/auth/v1/token" ? JsonContent.Create(new{access_token="access-token",refresh_token="refresh",expires_at=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),user=new{id=owner,user_metadata=new{display_name="Test"}}}) : JsonContent.Create(new HubPage([sound],false))})));
        await uiClient.SignIn("u@example.test","123456",CancellationToken.None);
        var hub=new SoundsHubWindow(window,uiClient){ShowActivated=false};hub.Show();await Task.Delay(100);hub.UpdateLayout();
        Check(Descendants<ListBox>(hub).Single().Items.Count==1&&Descendants<Button>(hub).Single(b=>b.Content is string s&&s=="Upload Sound").IsEnabled,"authenticated Hub renders catalogue and enables upload");
        Snapshot(hub,"sounds-hub-native.png");
        var account=new HubAccountWindow(hub,client){ShowActivated=false};account.Show();account.UpdateLayout();
        Check(Descendants<PasswordBox>(account).Count()==1&&Descendants<TextBox>(account).Any(t=>t.Name=="HubIdentifier")&&Descendants<CheckBox>(account).Any(t=>t.Name=="HubRememberMe"&&t.IsChecked!=true),"login offers username or email, password, and an optional 30-day remember toggle");
        Snapshot(account,"sounds-hub-account.png");account.Close();hub.Close();window.Close();
        rejectLogout=true;
        try{await client.SignOut(CancellationToken.None);}catch(HttpRequestException){}
        Check(client.Session==null&&!File.Exists(sessionFile),"sign-out clears local credentials even when the network is unavailable");
    }
}
