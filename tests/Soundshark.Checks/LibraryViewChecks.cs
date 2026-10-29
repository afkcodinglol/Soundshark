using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Soundshark;

internal static partial class Program
{
    static async Task LibraryViewChecks()
    {
        var window = new MainWindow { ShowActivated=false, Left=-20000, Top=-20000, Width=1240, Height=900 };
        window.Show(); await Task.Delay(150);
        ((TabControl)window.FindName("Tabs")).SelectedIndex=1;
        window.UpdateLayout(); await Task.Delay(100);
        var pane=Descendants<LibraryPane>(window).Single();
        var list=Descendants<ListBox>(pane).Single();
        var appearance=window.Settings.Appearance with { ReduceMotion=false, NewestFirst=false };
        window.Settings.Appearance=appearance; Theme.Apply(appearance);
        for(int i=0;i<600;i++) {
            using var writer=new NAudio.Wave.WaveFileWriter(Path.Combine(Paths.Library,$"Sound {i:000}.wav"),new NAudio.Wave.WaveFormat(48000,16,1));
            writer.Write(new byte[960],0,960);
        }
        await pane.Refresh(); await Task.Delay(300); window.UpdateLayout();
        Check(Descendants<SoundTilePanel>(list).Any(),"Library defaults to the existing virtualized grid");
        await pane.SwitchLibraryView(); window.UpdateLayout(); await Task.Delay(100);
        Check(Descendants<VirtualizingStackPanel>(list).Any() && !Descendants<SoundTilePanel>(list).Any(),"view switch installs virtualized list rows");
        Check(Settings.Load().LibraryListView,"List view preference survives settings reload");
        var cells=Descendants<ListBoxItem>(list).ToArray();
        Check(cells.Length>2 && cells.Length<65,"600 list sounds realize only nearby visible rows");
        Check(cells[1].TranslatePoint(new Point(),list).Y>cells[0].TranslatePoint(new Point(),list).Y && Math.Abs(cells[1].TranslatePoint(new Point(),list).X-cells[0].TranslatePoint(new Point(),list).X)<1,"list rows fill a single column");
        var first=cells[0];
        Check(Descendants<TextBlock>(first).Any(t=>t.Text.StartsWith("Play Count:")) && Descendants<Button>(first).Count()==3,"List view preserves per-sound counts and all playback actions");
        var preview=Descendants<Button>(first).Single(b=>Equals(b.Content,"Preview"));
        preview.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left) { RoutedEvent=Mouse.PreviewMouseDownEvent });
        Check(Mouse.Captured!=list && !pane.Arranging,"pressing a playback button does not arm drag ordering");
        var grip=Descendants<Border>(first).Single(b=>b.Name=="SoundDragHandle");
        var gripPress=new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left) { RoutedEvent=Mouse.PreviewMouseDownEvent };
        grip.RaiseEvent(gripPress);
        Check(gripPress.Handled,"row grip accepts the drag gesture without invoking playback");
        pane.CancelReorder();
        string[] Names()=>list.Items.Cast<LibraryPane.SoundRow>().Select(r=>Path.GetFileName(r.Path)).ToArray();
        var original=Names();
        pane.BeginReorder(0);
        pane.UpdateReorderPointer(first.TranslatePoint(new Point(20,first.ActualHeight/2),list)); pane.ShowDragPreview();
        var layer=System.Windows.Documents.AdornerLayer.GetAdornerLayer(list);
        Check(layer?.GetAdorners(list)?.Length>0 && Descendants<ListBoxItem>(list).Any(i=>i.Opacity<1),"drag displays a floating preview and dims its original row");
        pane.UpdateReorderPointer(new Point(30,list.ActualHeight-5));
        pane.ScrollReorderAtEdge(); window.UpdateLayout();
        Check(Descendants<ScrollViewer>(list).First().VerticalOffset>0,"dragging near the list edge automatically scrolls");
        pane.UpdateReorderPointer(new Point(30,list.ActualHeight/2));
        Snapshot(window,"library-drag-preview.png");
        pane.CancelReorder();
        Check(layer?.GetAdorners(list)?.Length is null or 0,"cancelling removes the floating drag preview");
        list.ScrollIntoView(list.Items[0]);window.UpdateLayout();
        Check(pane.BeginReorder(0) && pane.MoveReorder(3),"drag ordering moves a sound across list rows");
        Check(Names()[3]==original[0] && Names()[0]==original[1],"neighbors move out of the dragged sound's destination");
        Check(Descendants<ListBoxItem>(list).Any(i=>i.RenderTransform is TranslateTransform t && t.HasAnimatedProperties),"displaced rows animate into their new positions");
        pane.CancelReorder();
        Check(Names().SequenceEqual(original) && !pane.Arranging,"cancelling restores the original order");
        Check(Descendants<ListBoxItem>(list).All(i=>i.Opacity==1 && i.RenderTransform==Transform.Identity),"cancelling removes drag dimming and row transforms");
        pane.BeginReorder(0); pane.MoveReorder(3); await pane.CompleteReorder();
        var reordered=Names(); var saved=Settings.Load();
        Check(reordered[3]==original[0] && saved.LibraryManualOrder && saved.LibraryOrder.Take(4).SequenceEqual(reordered.Take(4)),"dropping saves the new custom order");
        Check(!saved.GroupLibraryByColor && !saved.Appearance.NewestFirst,"custom order clears automatic sorting choices");
        await pane.Refresh(); Check(Names().SequenceEqual(reordered),"watcher refresh preserves custom ordering");
        await pane.SwitchLibraryView(); window.UpdateLayout();
        Check(Descendants<SoundTilePanel>(list).Any() && !pane.BeginReorder(0),"grid view remains available and does not allow row dragging");
        await pane.SwitchLibraryView(); window.UpdateLayout();
        Check(Names().SequenceEqual(reordered),"switching back to List restores its saved custom order");
        var search=Descendants<TextBox>(pane).Single(t=>t.Name=="LibrarySearch");
        search.Text="Sound 00"; await pane.Refresh(); window.UpdateLayout();
        Check(list.Items.Count==10,"List supports existing name search");
        var fullBefore=Settings.Load().LibraryOrder.ToArray(); var filtered=Names();
        pane.BeginReorder(0); pane.MoveReorder(8); await pane.CompleteReorder();
        var filteredSaved=Settings.Load().LibraryOrder;
        Check(Names()[8]==filtered[0] && filteredSaved.Count==600 && filteredSaved.Distinct(StringComparer.OrdinalIgnoreCase).Count()==600,"filtered drag saves every sound exactly once");
        Check(fullBefore.Where(n=>!filtered.Contains(n)).SequenceEqual(filteredSaved.Where(n=>!filtered.Contains(n))),"filtered drag preserves hidden sounds' relative ordering");
        Check(fullBefore.Select((name,index)=>(name,index)).Where(p=>!filtered.Contains(p.name)).All(p=>filteredSaved[p.index]==p.name),"hidden sounds retain their slots during filtered ordering");
        search.Text=""; await pane.Refresh(); window.UpdateLayout();
        var beforePerformance=Names(); pane.BeginReorder(0); pane.MoveReorder(2);
        window.Settings.Appearance=appearance with {ReduceMotion=true}; Theme.Apply(window.Settings.Appearance); window.UpdateLayout();
        Check(!pane.Arranging && Names().SequenceEqual(beforePerformance),"enabling Performance Mode cancels an in-progress drag");
        Check(!pane.CanArrange && !pane.BeginReorder(0) && !pane.MoveReorder(2),"Performance Mode blocks all drag reordering");
        Check(Descendants<ListBoxItem>(list).All(i=>i.RenderTransform is not TranslateTransform t || !t.HasAnimatedProperties),"Performance Mode leaves no row animations running");
        Check(Descendants<Border>(list).Where(b=>b.Name=="SoundDragHandle").All(b=>!b.IsEnabled),"Performance Mode visibly disables drag grips");
        await pane.SwitchLibraryView(); await pane.SwitchLibraryView(); window.UpdateLayout();
        Check(Descendants<VirtualizingStackPanel>(list).Any() && !pane.CanArrange,"view switching continues to work in Performance Mode");
        Theme.Apply(appearance); window.Settings.Appearance=appearance; window.UpdateLayout();
        list.ScrollIntoView(list.Items[599]); window.UpdateLayout(); await Task.Delay(100);
        Check(list.ItemContainerGenerator.ContainerFromIndex(599) is ListBoxItem last && last.TranslatePoint(new Point(),list).Y<list.ActualHeight,"last sound remains reachable in List view");
        Check(Descendants<ListBoxItem>(list).Count()<65,"list scrolling recycles offscreen rows");
        list.ScrollIntoView(list.Items[0]); window.UpdateLayout(); await Task.Delay(80);
        Snapshot(window,"library-list-standard.png");
        window.Width=850; window.Height=700; Theme.Apply(appearance with {FontSize=19,CompactLibrary=false}); window.UpdateLayout(); await Task.Delay(80);
        foreach(var item in Descendants<ListBoxItem>(list).Take(3)) {
            Check(Descendants<Button>(item).All(b=> {var p=b.TranslatePoint(new Point(),item); return p.X>=0 && p.X+b.ActualWidth<=item.ActualWidth+1 && p.Y+b.ActualHeight<=item.ActualHeight+1;}),"list buttons fit at minimum window size and largest text");
        }
        Snapshot(window,"library-list-large-text.png");
        Theme.Apply(appearance); window.Width=1240; window.Height=900; window.UpdateLayout();
        // While a drag owns the order, watcher updates are queued and reconciled after cancellation.
        pane.BeginReorder(0);
        using(var writer=new NAudio.Wave.WaveFileWriter(Path.Combine(Paths.Library,"Newly added.wav"),new NAudio.Wave.WaveFormat(48000,16,1))) writer.Write(new byte[960],0,960);
        await pane.Refresh(); Check(list.Items.Count==600,"file refresh is deferred while arranging");
        pane.CancelReorder(); await Task.Delay(300); await pane.Refresh();
        Check(list.Items.Count==601 && Names().Last()=="Newly added.wav","new sounds append without disrupting existing manual order");
        pane.BeginReorder(0); pane.MoveReorder(2);
        string deleted=Names()[1]; File.Delete(Path.Combine(Paths.Library,deleted));
        await pane.CompleteReorder();
        Check(list.Items.Count==600 && !Settings.Load().LibraryOrder.Contains(deleted) && Settings.Load().LibraryOrder.Count==600,"deletion during dragging reconciles every remaining sound without stale order entries");
        search.Text="none match"; await pane.Refresh(); window.UpdateLayout();
        Check(list.Items.Count==0 && !pane.BeginReorder(0),"empty results clear list rows and cannot start a drag");
        window.Studio.Workspace.Dirty=false;window.Close();
        Console.WriteLine($"{passed} Library view checks passed");
    }
}

