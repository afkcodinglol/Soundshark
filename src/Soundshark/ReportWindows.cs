using System.IO;
using System.Text;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace Soundshark;

public sealed record HubLogFile(string Name, string Text);
public sealed record HubSupportReport(Guid Id, string Username, string Kind, string Body, string Status,
    [property:JsonPropertyName("sound_id")] Guid? SoundId,
    [property:JsonPropertyName("created_at")] DateTimeOffset CreatedAt, HubLogFile[]? Files)
{
    public string Heading => $"{(Kind == "sound" ? "Sound review" : Kind=="suggestion"?"Suggestion":"Problem report")} · {Username} · {CreatedAt.LocalDateTime:g}";
}
public sealed record HubReportPage(HubSupportReport[] Reports, bool HasMore);

public sealed class IssueReportWindow : HubAuthWindow
{
    readonly ComboBox category = new() { Name="SubmissionCategory",ItemsSource=new[]{"Report a problem","Suggestion"},SelectedIndex=0,MinWidth=220 };
    readonly TextBox description = new() { Name = "IssueDescription", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160, MaxLength = 8000, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly ListBox attachments = new() { MinHeight = 65, MaxHeight = 140, DisplayMemberPath = "Name" };
    readonly List<HubLogFile> files = [];
    readonly Button send, attach, remove;
    bool sent;
    public IssueReportWindow(Window owner, SoundsHubClient client) : base(owner,"Issues & Suggestions")
    {
        Width = 640; Height = 720;
        Panel.Children.Add(Ui.Text("Describe the problem or your suggestion. Your report goes to Soundshark’s private Owner inbox.",13,true));
        Field("Type",category);
        Panel.Children.Add(Ui.Text("5 suggestions per 24 hours · 6 problem / sound reports per 12 hours.",12,true));
        Field("Your report",description);
        attach = Action("Attach Logs (0/5)", () => { PickLogs(); return Task.CompletedTask; });
        remove = Action("Remove Selected", () => { if (attachments.SelectedItem is HubLogFile file) files.Remove(file); RefreshFiles(); return Task.CompletedTask; });
        Panel.Children.Add(Ui.Row(attach,remove)); Panel.Children.Add(attachments);
        Panel.Children.Add(Ui.Text("Up to 5 text logs (.log, .txt, .json), 512 KB each. Only selected files are shared with the Owner.",12,true));
        send = Action("Send Report", async () => {
            Guid id = await client.SubmitIssue(description.Text.Trim(),files.ToArray(),Lifetime.Token,category.SelectedIndex==1?"suggestion":"issue");
            if (ClosedWindow) return;
            sent = true; description.IsReadOnly = true;
            Notice("Report sent to the Owner. Reference: "+id.ToString("N")[..8]);
        });
        Panel.Children.Add(Ui.Row(send,Ui.Button("Close",Close))); Panel.Children.Add(Message);
        description.TextChanged += (_,_) => UpdateButtons(); attachments.SelectionChanged += (_,_) => UpdateButtons(); UpdateButtons();
    }
    void PickLogs()
    {
        var picker = new OpenFileDialog { Title = "Attach log files", Multiselect = true, Filter = "Text logs|*.log;*.txt;*.json" };
        if (picker.ShowDialog(this) != true) return;
        if (files.Count + picker.FileNames.Length > 5) { Error("Attach at most five logs. Remove a file before adding more."); return; }
        // Validate the entire selection before adding any files.
        var pending = new List<HubLogFile>();
        foreach (string path in picker.FileNames) {
            string name = Path.GetFileName(path);
            if (name.Length > 124 || !new[]{".log",".txt",".json"}.Contains(Path.GetExtension(name).ToLowerInvariant())) throw new InvalidDataException("Choose text log files.");
            if (new FileInfo(path).Length > 512*1024) throw new InvalidDataException(name+" is larger than 512 KB.");
            string text = File.ReadAllText(path,new UTF8Encoding(false,true));
            if (text.Contains('\0') || Encoding.UTF8.GetByteCount(text)>512*1024) throw new InvalidDataException(name+" is not a supported text log.");
            if (files.Concat(pending).Any(f=>f.Name.Equals(name,StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Each attached log needs a distinct filename.");
            pending.Add(new(name,text));
        }
        files.AddRange(pending); RefreshFiles();
    }
    void RefreshFiles() { attachments.ItemsSource = files.ToArray(); attach.Content = $"Attach Logs ({files.Count}/5)"; }
    protected override void UpdateButtons()
    {
        base.UpdateButtons();
        if (send == null) return;
        send.IsEnabled = !Busy && !sent && description.Text.Trim().Length >= 10;
        attach.IsEnabled = !Busy && !sent && files.Count < 5; remove.IsEnabled = !Busy && !sent && attachments.SelectedItem != null;
        description.IsEnabled = category.IsEnabled = !Busy && !sent;
    }
}

public sealed class AdminToolsWindow : HubAuthWindow
{
    readonly SoundsHubClient client;
    readonly ComboBox filter = new() { ItemsSource = new[]{"open","resolved","unfixable"},SelectedIndex = 0, Width = 150 };
    readonly ListBox reports = new() { Name = "OwnerReports", MinHeight = 180, MaxHeight = 280, DisplayMemberPath = "Heading" };
    readonly TextBox details = new() { Name = "ReportDetails", IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 150, MaxHeight = 250, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly ListBox logs = new() { DisplayMemberPath = "Name", MaxHeight = 100 };
    readonly Button previous,next,resolve,unfixable,reopen,saveLog;
    readonly Button openReport, removeSound;
    int page; bool hasMore;
    HubSupportReport? selected;
    public AdminToolsWindow(Window owner, SoundsHubClient client) : base(owner,"Admin Tools")
    {
        this.client = client; Width = 940; Height = 900;
        reports.DisplayMemberPath = ""; logs.DisplayMemberPath = "";
        StyleReportList(reports,"Heading"); StyleReportList(logs,"Name");
        Panel.Children.Add(Ui.Row(Action("Review Reports",LoadReports),filter));
        Panel.Children.Add(reports);
        previous = Action("Previous", async()=>{ page--; await LoadReports(); }); next = Action("Next",async()=>{page++; await LoadReports();});
        openReport = Action("Open Selected Report",OpenSelected); Panel.Children.Add(Ui.Row(previous,next,openReport));
        Panel.Children.Add(details); Panel.Children.Add(logs);
        saveLog = Action("Save Selected Log",SaveLog); Panel.Children.Add(Ui.Row(saveLog));
        resolve = Action("Mark Resolved",()=>Mark("resolved")); unfixable = Action("Mark Unfixable",()=>Mark("unfixable")); reopen = Action("Reopen",()=>Mark("open"));
        removeSound=Action("Remove Reported Sound",async()=>{
            if(selected?.SoundId is not {} id || MessageBox.Show(this,"Permanently remove this reported sound from the Hub?","Remove Sound",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes) return;
            await client.RemoveReportedSound(id,Lifetime.Token); if(!ClosedWindow) Notice("Sound removed. You can now mark this report resolved.");
        });
        Panel.Children.Add(Ui.Row(resolve,unfixable,reopen,removeSound,Ui.Button("Close",Close))); Panel.Children.Add(Message);
        filter.SelectionChanged += (_,_) => { page=0; selected=null; reports.ItemsSource=null; hasMore=false; details.Clear(); logs.ItemsSource=null; UpdateButtons(); };
        reports.SelectionChanged += (_,_) => { selected=null; details.Clear(); logs.ItemsSource=null; UpdateButtons(); };
        logs.SelectionChanged += (_,_) => UpdateButtons(); UpdateButtons();
    }
    static void StyleReportList(ListBox list,string property)
    {
        list.SetResourceReference(BackgroundProperty,"Surface"); list.SetResourceReference(ForegroundProperty,"Text"); list.SetResourceReference(BorderBrushProperty,"Border");
        var item = new FrameworkElementFactory(typeof(TextBlock)); item.SetBinding(TextBlock.TextProperty,new Binding(property));
        item.SetResourceReference(TextBlock.ForegroundProperty,"Text"); item.SetResourceReference(TextBlock.FontSizeProperty,"Font13");
        item.SetValue(TextBlock.PaddingProperty,new Thickness(12,10,12,10)); item.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);
        list.ItemTemplate = new DataTemplate { VisualTree = item };
    }
    async Task LoadReports()
    {
        var result = await client.Reports((string)filter.SelectedItem,page,Lifetime.Token);
        if (ClosedWindow) return;
        reports.ItemsSource = result.Reports; hasMore=result.HasMore;
        Notice($"Page {page+1} · {result.Reports.Length} reports · {filter.SelectedItem}. Select a report, then Open Selected Report.");
    }
    async Task OpenSelected()
    {
        if (reports.SelectedItem is not HubSupportReport report) return;
        var value = await client.ReportDetails(report.Id,Lifetime.Token); if (ClosedWindow) return;
        selected=value; details.Text = value.Heading+"\nStatus: "+value.Status+"\n"+(value.SoundId is {} id?"Sound ID: "+id+"\n":"")+"\n"+value.Body;
        logs.ItemsSource=value.Files??[];
    }
    async Task Mark(string status)
    {
        if (selected == null) return;
        await client.SetReportStatus(selected.Id,status,Lifetime.Token);
        if (!ClosedWindow) { await LoadReports(); Notice("Report marked "+status+"."); }
    }
    Task SaveLog()
    {
        if (logs.SelectedItem is not HubLogFile file) return Task.CompletedTask;
        var picker = new SaveFileDialog { Title="Save report log", FileName=Path.GetFileName(file.Name), Filter="Text log|*.txt" };
        if (picker.ShowDialog(this)==true) File.WriteAllText(picker.FileName,file.Text,Encoding.UTF8);
        return Task.CompletedTask;
    }
    protected override void UpdateButtons()
    {
        base.UpdateButtons(); if (previous==null) return;
        openReport.IsEnabled = !Busy && reports.SelectedItem != null;
        previous.IsEnabled=!Busy&&page>0; next.IsEnabled=!Busy&&hasMore;
        removeSound.IsEnabled = !Busy && selected?.SoundId != null;
        resolve.IsEnabled=unfixable.IsEnabled=reopen.IsEnabled=!Busy&&selected!=null;
        saveLog.IsEnabled=!Busy&&logs.SelectedItem!=null; filter.IsEnabled=reports.IsEnabled=logs.IsEnabled=!Busy;
    }
}
