using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using Soundshark;

internal static partial class Program
{
    static async Task HubRefreshChecks(Window owner, string input)
    {
        var user = Guid.NewGuid(); int reads = 0; bool failNextRead = false;
        HubSound Sound(string name) => new(Guid.NewGuid(), name, "TestUser", 1.1, 25000, new string('a',64), user);
        var rows = new List<HubSound> { Sound("Original") }; HubSound? uploaded = null;
        var other = Sound("Another user's new sound");
        var handler = new HubHandler(async (request, ct) => {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/auth/v1/token") return new(HttpStatusCode.OK) { Content = JsonContent.Create(new { access_token="access", refresh_token="refresh", expires_at=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(), user=new {id=user,user_metadata=new {display_name="TestUser"}} }) };
            if (path.EndsWith("/upload")) {
                _ = await request.Content!.ReadAsByteArrayAsync(ct); uploaded = Sound("New upload"); rows.Add(uploaded);
                return new(HttpStatusCode.Created) { Content = JsonContent.Create(uploaded) };
            }
            if (request.Method == HttpMethod.Delete) {
                rows.RemoveAll(s => path.EndsWith(s.Id.ToString()));
                if (!rows.Any(s=>s.Id==other.Id)) rows.Add(other);
                return new(HttpStatusCode.OK) { Content=JsonContent.Create(new {removed=true}) };
            }
            reads++;
            if (failNextRead) { failNextRead=false; return new(HttpStatusCode.ServiceUnavailable) { Content=JsonContent.Create(new {error="test failure"}) }; }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new HubPage(rows.ToArray(),false)) };
        });
        using var client = new SoundsHubClient(new("https://refresh-test.supabase.co/","sb_publishable_test"),handler);
        await client.SignIn("test@example.test","123456",CancellationToken.None);
        var hub = new SoundsHubWindow((MainWindow)owner,client) { ShowActivated=false }; hub.Show(); await hub.Pending; hub.UpdateLayout();
        var list = Descendants<ListBox>(hub).Single();
        var refresh = Descendants<Button>(hub).Single(b=>Equals(b.Content,"Refresh Sounds"));
        rows.Add(Sound("Published elsewhere")); int before=reads;
        refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await hub.Pending;
        Check(reads==before+1 && list.Items.Count==2,"Refresh Sounds fetches current server results and updates the Hub list");
        before=reads; await hub.UploadFile(input,"New upload");
        Check(reads==before+1 && list.Items.OfType<HubSound>().Any(s=>s.Id==uploaded!.Id),"successful upload automatically refreshes and displays the new server catalogue");
        before=reads; var removed=uploaded!; await hub.RemoveSound(removed);
        Check(reads==before+1 && !list.Items.OfType<HubSound>().Any(s=>s.Id==removed.Id) && list.Items.OfType<HubSound>().Any(s=>s.Id==other.Id),"successful deletion reloads server results instead of only removing a cached row");
        failNextRead=true; await hub.UploadFile(input,"New upload");
        Check(Descendants<TextBlock>(hub).Any(t=>t.Text.Contains("Shared New upload.")&&t.Text.Contains("Use Refresh Sounds")),"a successful upload followed by refresh failure is clearly distinguished from upload failure");
        refresh.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await hub.Pending;
        Check(list.Items.OfType<HubSound>().Any(s=>s.Id==uploaded!.Id),"manual refresh recovers the catalogue after a temporary refresh failure");
        Snapshot(hub,"hub-refresh-sounds.png"); hub.Close(); await hub.Pending;
    }
}
