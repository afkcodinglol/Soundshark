using System.Windows;
using System.Windows.Controls;
using Soundshark;

internal static partial class Program
{
    static async Task StudioLayoutChecks()
    {
        var window=new MainWindow {ShowActivated=false,Left=-20000,Top=-20000};window.Show();await Task.Delay(120);
        var appearance=window.Settings.Appearance;
        foreach(var mode in new[]{(1920d,1040d,"Abel",12d,"wide"),(1240d,940d,"Segoe UI",14d,"standard")}) {
            window.Width=mode.Item1;window.Height=mode.Item2;Theme.Apply(appearance with {Font=mode.Item3,FontSize=mode.Item4});
            Descendants<Expander>(window.Studio).Single().IsExpanded=true;
            await Task.Delay(120);window.UpdateLayout();
            var viewport=window.Studio.Children.OfType<ScrollViewer>().Single();
            Console.WriteLine($"{mode.Item5}: scrolling={viewport.ScrollableHeight:F1}, studio={window.Studio.ActualHeight:F1}");
            Check(viewport.ScrollableHeight<1,$"expanded Studio source panels and sliders fit without scrolling at {mode.Item5} size");
            var editing=Descendants<StackPanel>(window.Studio).Single(p=>p.Name=="StudioEditingControls");
            Check(editing.ActualHeight<(mode.Item5=="wide"?80:125),$"editing controls use a compact toolbar at {mode.Item5} size");
            var zoom=Descendants<Slider>(window).Single(s=>s.Name=="WaveZoom");var scroll=Descendants<Slider>(window).Single(s=>s.Name=="WaveScroll");
            Check(new[]{zoom,scroll}.All(s=>s.TransformToAncestor(viewport).Transform(new Point(0,s.ActualHeight)).Y<=viewport.ActualHeight),$"both waveform sliders remain visible without scrolling at {mode.Item5} size");
            Snapshot(window,$"studio-compact-{mode.Item5}.png");
        }
        window.Width=850;window.Height=700;await Task.Delay(100);
        var precise=Descendants<StackPanel>(window.Studio).Single(p=>p.Name=="PreciseSelection");
        Console.WriteLine($"Minimum: window={window.ActualHeight}, studio={window.Studio.ActualHeight}, host={((ContentControl)window.FindName("StudioHost")).ActualHeight}, viewportMax={window.Studio.Children.OfType<ScrollViewer>().Single().MaxHeight}, preciseBottom={precise.TransformToAncestor(window).Transform(new Point(0,precise.ActualHeight)).Y}");
        Check(precise.TransformToAncestor(window).Transform(new Point(precise.ActualWidth,precise.ActualHeight)).Y<window.ActualHeight,"precise selection stays accessible at minimum window size");
        Snapshot(window,"studio-compact-minimum.png");
        Descendants<Expander>(window.Studio).Single().IsExpanded=false;await Task.Delay(60);
        var sliders=Descendants<Slider>(window.Studio).OrderBy(s=>s.TransformToAncestor(window).Transform(new Point()).X).ToArray();
        Check(sliders.Zip(sliders.Skip(1)).All(p=>p.First.TransformToAncestor(window).Transform(new Point(p.First.ActualWidth,0)).X<=p.Second.TransformToAncestor(window).Transform(new Point()).X),"three sliders do not overlap at minimum window width");
        Snapshot(window,"studio-three-sliders-minimum.png");
        Theme.Apply(appearance);window.Studio.Workspace.Dirty=false;window.Close();Console.WriteLine($"{passed} compact Studio layout checks passed");
    }
}
