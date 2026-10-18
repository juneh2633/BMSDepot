using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Depot.Engine;

namespace Depot.Desktop;
public sealed record SongRow(Song Song,string Level,string State)
{
    public string Title=>Song.Title;
    public string Artist=>Song.Artist;
}
public partial class Home:Window
{
    private Preferences settings=new();
    private Catalog? catalog;
    private Library? library;
    private HashSet<string> installed=[];
    private bool busy,ready;
    private CancellationTokenSource? downloadCancellation;
    private string queueProgress="";
    private readonly CancellationTokenSource lifetime=new();
    private readonly DispatcherTimer searchTimer=new(){Interval=TimeSpan.FromMilliseconds(250)};
    private readonly DispatcherTimer cleanupTimer=new(){Interval=TimeSpan.FromMinutes(10)};
    private string SongsRoot=>Path.Combine(Preferences.Home,"library","난이도표");
    private static bool GameRunning()
    {
        var processes=Process.GetProcesses();
        try{return processes.Any(p=>{try{return p.ProcessName.StartsWith("OpenLR2",StringComparison.OrdinalIgnoreCase)||p.ProcessName.Equals("LR2body",StringComparison.OrdinalIgnoreCase);}catch(Exception ex) when(ex is InvalidOperationException or Win32Exception){return false;}});}
        finally{foreach(var process in processes)process.Dispose();}
    }
    public Home()
    {
        InitializeComponent();Loaded+=async(_,_)=>await Run(Initialize);
        searchTimer.Tick+=(_,_)=>{searchTimer.Stop();RefreshList();};
        cleanupTimer.Tick+=async(_,_)=>{if(!busy&&library is not null&&!GameRunning())await Run(async()=>{await Task.Run(()=>library.Cleanup());await RefreshInstalled();});};
    }
    private async Task Initialize()
    {
        settings=Preferences.Load();Capacity.Text=settings.LimitGiB.ToString();Days.Text=settings.KeepDays.ToString();
        catalog=await Catalog.Load(Path.Combine(AppContext.BaseDirectory,"catalog.json"),lifetime.Token);
        library=new(Path.Combine(Preferences.Home,"library"),settings){GameRunning=GameRunning};
        library.Changed+=status=>Dispatcher.BeginInvoke(()=>ShowStatus(status));ManagedFolder.Text=SongsRoot;
        var tables=catalog.Songs.SelectMany(s=>s.Tables).Select(t=>t.Table).Distinct().Order().ToList();tables.Insert(0,"전체 카탈로그");
        TableBox.ItemsSource=tables;TableBox.SelectedItem=tables.Contains(settings.Table)?settings.Table:tables[0];
        ready=true;await RefreshInstalled();cleanupTimer.Start();
        Status.Text="준비 완료 · 곡을 다운로드한 뒤 OpenLR2 선곡 화면에서 F8을 누르세요.";
    }
    private async Task RefreshInstalled()
    {
        installed=await Task.Run(()=>library!.InstalledHashes());
        var bytes=await Task.Run(()=>library!.UsedBytes);Storage.Text=$"{bytes/1073741824d:N2} / {settings.LimitGiB} GiB · 최대 200 GiB";RefreshList();
    }
    private void RefreshList()
    {
        if(!ready||catalog is null)return;
        var selectedIds=Songs.SelectedItems.Cast<SongRow>().Select(row=>row.Song.Id).ToHashSet();
        var table=TableBox.SelectedItem as string;var term=Search.Text.Trim();
        var rows=catalog.Songs.Where(s=>(table=="전체 카탈로그"||s.Tables.Any(t=>t.Table==table))&&(term.Length==0||s.Title.Contains(term,StringComparison.OrdinalIgnoreCase)||s.Artist.Contains(term,StringComparison.OrdinalIgnoreCase))&&(InstalledOnly.IsChecked!=true||installed.Contains(s.Md5)))
            .Select(s=>new SongRow(s,string.Join(", ",s.Tables.Where(t=>table=="전체 카탈로그"||t.Table==table).Select(t=>$"{t.Table} {t.Level}").Distinct()),installed.Contains(s.Md5)?"설치됨":"미설치")).ToList();
        Songs.ItemsSource=rows;foreach(var row in rows.Where(row=>selectedIds.Contains(row.Song.Id)))Songs.SelectedItems.Add(row);
        Summary.Text=$"{rows.Count:N0}개 채보 · 게임에는 설치가 완료된 실제 곡만 표시됩니다.";UpdateControls();
    }
    private void TableChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!ready)return;settings.Table=(string?)TableBox.SelectedItem??"전체 카탈로그";
        try{settings.Save();}catch(Exception ex){Status.Text=ex.Message;}RefreshList();
    }
    private void SearchChanged(object sender,TextChangedEventArgs e){searchTimer.Stop();searchTimer.Start();}
    private void FilterChanged(object sender,RoutedEventArgs e)=>RefreshList();
    private void SongChanged(object sender,SelectionChangedEventArgs e){SelectedTitle.Text=Songs.SelectedItems.Count>1?$"{Songs.SelectedItems.Count}개 채보 선택":(Songs.SelectedItem as SongRow)?.Title??"목록에서 곡을 선택하세요.";UpdateControls();}
    private void CopyFolder(object sender,RoutedEventArgs e){Clipboard.SetText(SongsRoot);Status.Text="난이도표 폴더 경로를 복사했습니다. 이전 songs/list 경로 대신 이 경로만 OpenLR2에 등록하세요.";}
    private void OpenManagedFolder(object sender,RoutedEventArgs e){Directory.CreateDirectory(SongsRoot);Process.Start(new ProcessStartInfo("explorer.exe",SongsRoot){UseShellExecute=true});}
    private async void RefreshLibrary(object sender,RoutedEventArgs e)
    {await Run(async()=>{await RefreshInstalled();Status.Text="설치 상태를 갱신했습니다. 게임의 곡 목록은 OpenLR2에서 F8로 갱신하세요.";});}
    private async void OrganizeLibrary(object sender,RoutedEventArgs e)
    {
        if(GameRunning()){Status.Text="기존 폴더를 재구성하려면 OpenLR2를 종료하세요. 새 곡 다운로드는 게임 중에도 가능합니다.";return;}
        await Run(async()=>
        {
            var count=await library!.OrganizeFolders(catalog!.Songs,lifetime.Token);
            Status.Text=$"{count}개 채보를 난이도표·레벨 폴더로 정리했습니다. 새 관리 경로를 OpenLR2에 등록하세요.";
        });
    }
    private async void DownloadSong(object sender,RoutedEventArgs e)
    {
        if(busy)return;
        var selected=Songs.SelectedItems.Cast<SongRow>().ToList();if(selected.Count==0)return;
        var selectedTable=TableBox.SelectedItem as string;
        await Run(async()=>
        {
            using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            downloadCancellation=cancellation;UpdateControls();
            var completed=0;var failures=new List<string>();var cancelled=false;
            try
            {
                for(var index=0;index<selected.Count;index++)
                {
                    if(cancellation.IsCancellationRequested){cancelled=true;break;}
                    var row=selected[index];queueProgress=$"[{index+1}/{selected.Count}] {row.Title} · ";
                    try
                    {
                        var chart=await library!.Ensure(row.Song,cancellation.Token);
                        await library.PublishFolders(row.Song,chart,selectedTable=="전체 카탈로그"?null:selectedTable,cancellation.Token);completed++;
                    }
                    catch(OperationCanceledException) when(cancellation.IsCancellationRequested){cancelled=true;break;}
                    catch(Exception ex) when(ex is IOException or HttpRequestException or UnauthorizedAccessException or OperationCanceledException)
                    {failures.Add(row.Title+": "+ex.Message);}
                }
                await RefreshInstalled();
                var pending=selected.Count-completed-failures.Count;
                Status.Text=$"{(cancelled?"취소됨":"처리 완료")} · 완료 {completed} / 실패 {failures.Count} / 미처리 {pending} · OpenLR2 선곡 화면에서 F8로 갱신하세요.";
                if(failures.Count>0)
                {
                    Error.Text=string.Join("\n\n",failures);ErrorPanel.Visibility=Visibility.Visible;
                    await File.AppendAllTextAsync(Path.Combine(Preferences.Home,"errors.log"),DateTimeOffset.Now+"\n"+Error.Text+"\n");
                }
            }
            finally{downloadCancellation=null;queueProgress="";UpdateControls();}
        });
    }
    private void CancelDownload(object sender,RoutedEventArgs e)
    {downloadCancellation?.Cancel();Status.Text="취소 요청 중 · 완료된 곡은 유지하고 남은 다운로드를 중단합니다.";UpdateControls();}
    private async void SaveSettings(object sender,RoutedEventArgs e)
    {
        await Run(async()=>
        {
            if(!int.TryParse(Capacity.Text,out var capacity)||capacity is <1 or >200||!int.TryParse(Days.Text,out var days)||days is <1 or >3650)throw new IOException("용량은 1~200 GiB, 기간은 1~3650일로 입력하세요.");
            settings.LimitGiB=capacity;settings.KeepDays=days;settings.Save();
            await Task.Run(()=>library!.Cleanup());await RefreshInstalled();
            Status.Text=GameRunning()?"저장했습니다. 기존 곡 정리는 게임 종료 후 진행합니다.":"보관 설정을 저장했습니다.";
        });
    }
    private void ShowStatus(OperationStatus status)
    {
        Progress.Visibility=status.Phase is "READY" or "FAILED"?Visibility.Collapsed:Visibility.Visible;Progress.IsIndeterminate=status.Total is null;
        if(status.Total is >0)Progress.Value=status.Received*100d/status.Total.Value;
        Status.Text=queueProgress+status.Message+(status.Total is >0?$" · {status.Received/1048576d:N1} / {status.Total.Value/1048576d:N1} MiB":"");
    }
    private void UpdateControls()
    {if(DownloadButton is null||CancelButton is null)return;DownloadButton.IsEnabled=ready&&!busy&&Songs.SelectedItems.Count>0;SaveButton.IsEnabled=ready&&!busy;RefreshButton.IsEnabled=ready&&!busy;OrganizeButton.IsEnabled=ready&&!busy;DownloadButton.Content=Songs.SelectedItems.Count>1?$"선택한 {Songs.SelectedItems.Count}개 순서대로 받기":Songs.SelectedItem is SongRow row&&installed.Contains(row.Song.Md5)?"설치 확인·폴더 연결":"선택한 곡 다운로드";CancelButton.Visibility=downloadCancellation is null?Visibility.Collapsed:Visibility.Visible;CancelButton.IsEnabled=downloadCancellation?.IsCancellationRequested==false;}
    private async Task Run(Func<Task> action)
    {
        if(busy)return;busy=true;ErrorPanel.Visibility=Visibility.Collapsed;UpdateControls();
        try{await action();}
        catch(Exception ex){Status.Text="작업을 완료하지 못했습니다.";Error.Text=ex.Message;ErrorPanel.Visibility=Visibility.Visible;Directory.CreateDirectory(Preferences.Home);await File.AppendAllTextAsync(Path.Combine(Preferences.Home,"errors.log"),DateTimeOffset.Now+"\n"+ex+"\n");}
        finally{busy=false;Progress.Visibility=Visibility.Collapsed;UpdateControls();}
    }
    private void ClosingWindow(object? sender,CancelEventArgs e)
    {
        if(busy){e.Cancel=true;WindowState=WindowState.Minimized;Status.Text="다운로드가 끝날 때까지 최소화합니다.";return;}
        searchTimer.Stop();cleanupTimer.Stop();lifetime.Cancel();library?.Dispose();
    }
}
