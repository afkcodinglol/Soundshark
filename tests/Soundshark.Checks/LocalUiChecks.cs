using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Soundshark;
using Soundshark.Core;

internal static partial class Program
{
    static async Task LocalUiChecks()
    {
        var main = new MainWindow { ShowActivated=false, Left=-20000, Top=-20000 }; main.Show(); await Task.Delay(150);
        var footer=(Grid)main.FindName("Footer"); var buttons=Descendants<Button>(footer).ToArray();
        Check(buttons.Length==8 && buttons.All(b=>b.ActualWidth==158 && b.ActualHeight==36),"footer actions have consistent dimensions");
        foreach(var pair in new[]{new[]{"UpdateAppButton","RollbackAppButton"},new[]{"UpdateTools","RollbackTools"}}) {
            var first=(Button)main.FindName(pair[0]);var second=(Button)main.FindName(pair[1]);
            Check(first.TranslatePoint(new Point(),main).X==second.TranslatePoint(new Point(),main).X && first.TranslatePoint(new Point(),main).Y<second.TranslatePoint(new Point(),main).Y,"update and rollback actions form vertical pairs "+pair[0]);
        }
        Check(!Descendants<TextBlock>(main).Any(t=>t.Text.Contains("Ready when you are")||t.Text.Contains("Local Audio Studio")||t.Text.Contains("You're up to date")),"removed idle and version text stays absent");
        Check(Descendants<Button>(main).Single(b=>b.Name=="AdminToolsButton").Visibility==Visibility.Collapsed,"Admin Tools is hidden without server Owner authorization");
        var settings=new UiSettingsWindow(main){ShowActivated=false};settings.Show();await Task.Delay(60);
        Check(Descendants<Button>(settings).Any(b=>Equals(b.Content,"Custom Font"))&&!Descendants<ComboBox>(settings).Any(c=>c.Items.Contains("Light")),"font import is beside chooser and obsolete appearance dropdown is removed");
        Snapshot(settings,"custom-font-settings.png");settings.Close();
        string fontPath=Path.GetFullPath("assets/Fonts/Lato-Regular.ttf");
        if(!File.Exists(fontPath))fontPath=Directory.GetFiles(Path.GetFullPath("assets/Fonts"),"*.ttf").First();
        var names=CustomFonts.Import(fontPath);string original=Path.Combine(Paths.Cache,"original-font.ttf");File.Copy(fontPath,original);
        var imported=CustomFonts.Import(original);File.Delete(original);
        Check(names.Length>0 && imported.All(n=>Theme.Fonts.Contains(n)&&Theme.Family(n).GetTypefaces().Any(t=>t.TryGetGlyphTypeface(out _))),"imported font is copied into managed storage and resolves after original file is removed");
        string invalid=Path.Combine(Paths.Cache,"bad.ttf");File.WriteAllText(invalid,"not a font");
        try{CustomFonts.Import(invalid);throw new Exception("Invalid font was accepted");}catch(ArgumentException){Check(true,"invalid font cannot replace the application font");}
        string pcm=Paths.NewPcm();using(var file=File.Create(pcm))file.SetLength(48000L*8*180);
        await main.Studio.LoadPcm(pcm,"Three minute track");await Task.Delay(100);
        Check(main.Studio.Wave.Zoom==1 && main.Studio.Wave.Scroll==0 && main.Studio.Wave.ViewFrames==main.Studio.Workspace.Frames,"three minute audio opens fitted to the entire waveform");
        var zoom=Descendants<Slider>(main).Single(s=>s.Name=="WaveZoom");var scroll=Descendants<Slider>(main).Single(s=>s.Name=="WaveScroll");
        zoom.Value=2;scroll.Value=.7;
        Check(main.Studio.Wave.Zoom==4 && Math.Abs(main.Studio.Wave.Scroll-.7)<.001 && Descendants<Thumb>(zoom).Any(t=>t.ActualWidth>=24),"large zoom and scroll controls change the actual waveform viewport");
        main.Studio.Wave.Scroll=.25;Check(scroll.Value==.25,"playhead and wheel scroll changes stay synchronized with slider");
        zoom.Value=0;Check(!scroll.IsEnabled,"scroll control disables when full track is visible");
        Snapshot(main,"studio-local-dark.png");
        var theme=(Button)main.FindName("ThemeSwitch");theme.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await Task.Delay(100);
        Check(Theme.Current.LightMode && Settings.Load().Appearance.LightMode,"header theme switch applies and persists light mode");
        Snapshot(main,"studio-local-light.png");
        main.Width=850;main.Height=700;await Task.Delay(100);Snapshot(main,"studio-local-small.png");
        Check(buttons.All(b=>b.TranslatePoint(new Point(b.ActualWidth,b.ActualHeight),main).X<=main.ActualWidth),"footer fits minimum window width");
        main.Width=1240;main.Height=940;
        var tabs=(TabControl)main.FindName("Tabs");tabs.SelectedIndex=1;await Task.Delay(100);
        Check(!Descendants<TextBlock>(main).Any(t=>t.Text.StartsWith("Played:")),"Library has no aggregate playback count");Snapshot(main,"library-play-counter.png");
        string librarySound=Path.Combine(Paths.Library,"Counter fixture.wav");
        using(var writer=new NAudio.Wave.WaveFileWriter(librarySound,new NAudio.Wave.WaveFormat(48000,16,2))) writer.Write(new byte[48000*4*2],0,48000*4*2);
        await main.RefreshLibrary();
        var library=(LibraryPane)((ContentControl)main.FindName("LibraryHost")).Content;
        var binding=new SoundboardBinding { Route=SoundboardRoute.Preview,SoundFilename=Path.GetFileName(librarySound) };
        library.Execute(binding);
        var deadline=DateTime.UtcNow.AddSeconds(8);while((main.Settings.LibraryPlayCounts.GetValueOrDefault("Counter fixture.wav")==0 || Settings.Load().LibraryPlayCounts.GetValueOrDefault("Counter fixture.wav")==0) && DateTime.UtcNow<deadline)await Task.Delay(30);
        Check(main.Settings.LibraryPlayCounts.GetValueOrDefault("Counter fixture.wav")==1 && Settings.Load().LibraryPlayCounts.GetValueOrDefault("Counter fixture.wav")==1,"actual Library playback increments and persists the counter once");
        library.Execute(binding);await Task.Delay(50);
        Check(main.Settings.LibraryPlayCounts.GetValueOrDefault("Counter fixture.wav")==1,"stopping an active preview does not count as another play");
        library.Execute(binding with { SoundFilename="missing.wav" });
        Check(main.Settings.LibraryPlayCounts.GetValueOrDefault("Counter fixture.wav")==1,"missing sounds do not increment playback count");
        bool owner=true;var report=new HubSupportReport(Guid.NewGuid(),"tester","issue","Please improve this sound editing feature.","open",null,DateTimeOffset.UtcNow,[new("soundshark.log","Example diagnostic log")]);
        var handler=new HubHandler((request,ct)=>{
            object payload=request.RequestUri!.AbsolutePath switch {
                "/auth/v1/token"=>new{access_token="test",refresh_token="refresh",expires_at=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),user=new{id=Guid.NewGuid(),user_metadata=new{display_name="Pheex"}}},
                "/functions/v1/sounds-hub/account"=>new{isOwner=owner},
                "/functions/v1/sounds-hub/admin/reports"=>new HubReportPage([report],false),
                _=>report
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(payload)});
        });
        using var client=new SoundsHubClient(new("https://test.supabase.co/","sb_publishable_test"),handler);
        await client.SignIn("test@example.test","123456",CancellationToken.None);await client.RefreshRole(CancellationToken.None);Check(client.IsOwner,"client Owner state comes from authenticated server role response");
        var issues=new IssueReportWindow(main,client){ShowActivated=false};issues.Show();await Task.Delay(70);Snapshot(issues,"issues-suggestions.png");issues.Close();
        var admin=new AdminToolsWindow(main,client){ShowActivated=false};admin.Show();await Task.Delay(70);
        Descendants<Button>(admin).Single(b=>Equals(b.Content,"Review Reports")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await admin.Pending;
        var reports=Descendants<ListBox>(admin).Single(l=>l.Name=="OwnerReports");reports.SelectedIndex=0;
        Descendants<Button>(admin).Single(b=>Equals(b.Content,"Open Selected Report")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await admin.Pending;
        Check(Descendants<TextBox>(admin).Single(t=>t.Name=="ReportDetails").Text.Contains(report.Body),"Owner report viewer opens report body and attachment details");Snapshot(admin,"admin-review-reports.png");admin.Close();
        owner=false;await client.RefreshRole(CancellationToken.None);Check(!client.IsOwner,"revoked Owner access clears local privilege state");
        main.Studio.Workspace.Dirty=false;main.Close();Console.WriteLine($"{passed} local feature checks passed");
    }
}
