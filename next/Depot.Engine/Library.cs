using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Depot.Engine;

public sealed record InstallRecord(string Work,DateTimeOffset Used,long Bytes,Dictionary<string,string> Charts);
public sealed record OperationStatus(string Phase,string Message,long Received=0,long? Total=null);
public sealed class Library : IDisposable
{
    public string Root {get;}
    private readonly string cache,records,stage;
    private readonly Transfer transfer=new();
    private readonly SemaphoreSlim gate=new(1,1);
    private readonly Preferences settings;
    private readonly ChartFolders folders;
    public event Action<OperationStatus>? Changed;
    public void NotifyFailure(string message)=>Changed?.Invoke(new("FAILED",message));
    public Func<bool> GameRunning {get;set;}=()=>false;
    public bool Busy {get;private set;}
    public string? ProtectedWork {get;set;}
    public Library(string root,Preferences settings)
    {Root=Path.GetFullPath(root);this.settings=settings;cache=Path.Combine(Root,"songs");records=Path.Combine(Root,"installed");stage=Path.Combine(Root,"temporary");foreach(var dir in new[]{cache,records,stage})Directory.CreateDirectory(dir);folders=new(Root,cache);}
    public string FoldersRoot=>folders.Root;
    public Task PublishFolders(Song song,string chart,string? table,CancellationToken ct=default)=>Task.Run(()=>{folders.GameRunning=GameRunning;folders.Publish(song,chart,table,ct);},ct);
    public async Task<int> OrganizeFolders(IEnumerable<Song> songs,CancellationToken ct=default)
    {
        var count=0;
        foreach(var song in songs)
        {
            ct.ThrowIfCancellationRequested();var record=Read(song.Work);
            if(record is null||!record.Charts.TryGetValue(song.Md5,out var relative))continue;
            var chart=Transfer.Inside(Path.Combine(cache,song.Work),relative);if(!File.Exists(chart))continue;
            Report("INSTALLING",$"난이도 폴더를 정리합니다 · {song.Title}");await PublishFolders(song,chart,null,ct);count++;
        }
        return count;
    }
    private string RecordPath(string work)=>Transfer.Inside(records,work+".json");
    private InstallRecord? Read(string work)
    {
        var file=RecordPath(work);if(!File.Exists(file))return null;
        try{return JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(file));}
        catch(JsonException){return null;}
    }
    public string SongsRoot=>cache;
    public HashSet<string> InstalledHashes()
    {
        var hashes=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in Directory.EnumerateFiles(records,"*.json"))
        {
            try
            {
                var record=JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(file));if(record is null)continue;
                foreach(var chart in record.Charts)
                    if(File.Exists(Transfer.Inside(Path.Combine(cache,record.Work),chart.Value)))hashes.Add(chart.Key);
            }
            catch(Exception ex) when(ex is IOException or JsonException or ArgumentException){}
        }
        return hashes;
    }
    private void Save(InstallRecord record)
    {var path=RecordPath(record.Work);File.WriteAllText(path+".new",JsonSerializer.Serialize(record));File.Move(path+".new",path,true);}
    public void Touch(Song song)
    {ProtectedWork=song.Work;var record=Read(song.Work);if(record is not null)Save(record with{Used=DateTimeOffset.UtcNow});}
    public async Task<string?> Ready(Song song,CancellationToken ct=default)
    {
        var record=Read(song.Work);if(record is null||!record.Charts.TryGetValue(song.Md5,out var relative))return null;
        var path=Transfer.Inside(Path.Combine(cache,song.Work),relative);
        if(!File.Exists(path))return null;
        try{if(!await HashMatches(path,song,ct))return null;await VerifyAudio(path,Path.Combine(cache,song.Work),ct);return path;}
        catch(IOException){return null;}
    }
    public async Task<string> Ensure(Song song,CancellationToken ct=default)
    {
        await gate.WaitAsync(ct);Busy=true;ProtectedWork=song.Work;
        try
        {
            Report("CHECKING","설치된 곡을 확인합니다");var ready=await Ready(song,ct);if(ready is not null){Touch(song);return ready;}
            var workRoot=Transfer.Inside(cache,song.Work);
            if(Directory.Exists(workRoot))
            {
                // Recover an interrupted/missing receipt from the already downloaded chart.
                var cached=await FindChart(song,workRoot,ct);
                if(cached is not null)
                {
                    var valid=true;try{await VerifyAudio(cached,workRoot,ct);}catch(IOException){valid=false;}
                    if(valid){await Register(song,workRoot,ct);Report("READY","기존 곡의 설치 기록을 복구했습니다");return cached;}
                }
                var added=await AddChart(song,workRoot,ct);
                if(added is not null){await Register(song,workRoot,ct);return added;}
                if(GameRunning())throw new IOException("기존 곡을 교체해야 합니다. 플레이 중인 파일 보호를 위해 게임을 종료한 뒤 다시 다운로드하세요.");
            }
            var sources=song.Sources.ToList();var errors=new List<string>();var lookedUp=false;var seen=new HashSet<string>();
            for(var index=0;index<=sources.Count;index++)
            {
                if(index==sources.Count)
                {
                    if(lookedUp)break;lookedUp=true;Report("CHECKING","원곡을 온라인에서 찾습니다");
                    try{sources.AddRange(await transfer.Find(song,ct));}catch(Exception ex) when(ex is HttpRequestException or IOException or OperationCanceledException&&!ct.IsCancellationRequested){errors.Add("원곡 검색: "+ex.Message);}
                    if(index==sources.Count)break;
                }
                var source=sources[index];if(!seen.Add(source.FileId+"|"+source.Url))continue;
                ct.ThrowIfCancellationRequested();var temp=Transfer.Inside(stage,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
                var committed=false;
                try
                {
                    Cleanup(source.Bytes>0?source.Bytes*3:512L*1024*1024,song.Work);
                    var archive=Path.Combine(temp,"download");var content=Path.Combine(temp,"content");Directory.CreateDirectory(content);
                    Report("DOWNLOADING","곡 파일을 다운로드합니다");
                    await transfer.Download(source,archive,FreeBudget(),(received,total)=>Changed?.Invoke(new("DOWNLOADING","곡 파일을 다운로드합니다",received,total)),ct);
                    Report("VERIFYING","압축을 풀고 실제 채보를 확인합니다");
                    await Task.Run(()=>Transfer.Extract(archive,content,FreeBudget(),ct),ct);
                    var selected=await FindChart(song,content,ct)??await AddChart(song,content,ct);
                    if(selected is null)throw new IOException("받은 원곡에서 선택한 채보를 찾지 못했습니다.");
                    await VerifyAudio(selected,content,ct);var relative=Path.GetRelativePath(content,selected);
                    ct.ThrowIfCancellationRequested();
                    Report("INSTALLING","플레이할 곡을 저장합니다");
                    // An existing cache stays intact if a replacement fails before commit.
                    var backup=Path.Combine(temp,"previous");var existing=Directory.Exists(workRoot);
                    if(existing&&GameRunning())throw new IOException("게임이 실행되어 기존 곡 교체를 중단했습니다. 게임 종료 후 다시 시도하세요.");
                    if(existing)Directory.Move(workRoot,backup);
                    try
                    {
                        Directory.Move(content,workRoot);await Register(song,workRoot,CancellationToken.None);committed=true;
                    }
                    catch{if(Directory.Exists(workRoot))Directory.Delete(Transfer.Inside(cache,workRoot),true);if(existing)Directory.Move(backup,workRoot);throw;}
                    var result=Transfer.Inside(workRoot,relative);Report("READY","플레이 준비 완료");return result;
                }
                catch(Exception ex) when(ex is IOException or HttpRequestException or SharpCompress.Common.SharpCompressException){errors.Add(source.Label+": "+ex.Message);}
                finally
                {
                    // If rollback itself failed, keep its backup for recovery.
                    if(committed||!Directory.Exists(Path.Combine(temp,"previous")))
                        try{if(Directory.Exists(temp))Directory.Delete(Transfer.Inside(stage,temp),true);}
                        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
                }
            }
            throw new IOException("곡 설치에 실패했습니다.\n"+string.Join("\n",errors.TakeLast(4)));
        }
        finally{Busy=false;gate.Release();}
    }
    private async Task<string?> AddChart(Song song,string root,CancellationToken ct)
    {
        var folders=Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Where(Transfer.IsChart).Select(Path.GetDirectoryName).Distinct().ToList();
        if(folders.Count==0)return null;
        var candidates=new List<string>();var createdFiles=new List<string>();
        foreach(var extension in new[]{"bms","bme","bml","pms"})
        {
            var file=Transfer.Inside(stage,Guid.NewGuid().ToString("N")+"."+extension);
            var json=JsonSerializer.Serialize(new{relativePath="Sabuns/"+song.Md5[..2]+"/"+song.Md5+"."+extension});
            var id=Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+','-').Replace('/','_');
            try
            {
                await transfer.Download(new("차분","",id,0),file,32*1024*1024,(_,_)=>{},ct);
                if(!await HashMatches(file,song,ct))continue;
                foreach(var folder in folders)
                {
                    ct.ThrowIfCancellationRequested();
                    var target=Transfer.Inside(root,Path.Combine(folder!,"depot-"+song.Md5+"."+extension));
                    var created=!File.Exists(target);
                    if(created){File.Copy(file,target);createdFiles.Add(target);}else if(!await HashMatches(target,song,ct))continue;
                    try{await VerifyAudio(target,root,ct);candidates.Add(target);}
                    catch(IOException){if(created)TryDeleteFile(target);}
                    catch(OperationCanceledException){if(created)TryDeleteFile(target);throw;}
                }
                if(candidates.Count>0)break;
            }
            catch(HttpRequestException){}
            catch(OperationCanceledException){foreach(var created in createdFiles)TryDeleteFile(created);throw;}
            finally{TryDeleteFile(file);}
        }
        if(candidates.Count==1)return candidates[0];
        // Valid pre-existing chart files belong to the cache and must not be removed.
        foreach(var created in createdFiles)TryDeleteFile(created);
        return null;
    }
    private static void TryDeleteFile(string path)
    {try{if(File.Exists(path))File.Delete(path);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}}
    private async Task Register(Song song,string root,CancellationToken ct)
    {
        var charts=new Dictionary<string,string>();
        foreach(var path in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Where(Transfer.IsChart))
        {var hash=await Md5(path,ct);charts.TryAdd(hash,Path.GetRelativePath(root,path));}
        Save(new(song.Work,DateTimeOffset.UtcNow,Size(root),charts));
    }
    private static async Task<string?> FindChart(Song song,string root,CancellationToken ct)
    {foreach(var path in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Where(Transfer.IsChart))if(await HashMatches(path,song,ct))return path;return null;}
    private static async Task<string> Md5(string path,CancellationToken ct)
    {await using var input=File.OpenRead(path);return Convert.ToHexStringLower(await MD5.HashDataAsync(input,ct));}
    private static async Task<bool> HashMatches(string path,Song song,CancellationToken ct)
    {
        if(await Md5(path,ct)!=song.Md5)return false;if(song.Sha256.Length==0)return true;
        await using var input=File.OpenRead(path);return Convert.ToHexStringLower(await SHA256.HashDataAsync(input,ct))==song.Sha256;
    }
    public static async Task VerifyAudio(string chart,string root,CancellationToken ct)
    {
        var bytes=await File.ReadAllBytesAsync(chart,ct);Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);string text;
        try{text=new UTF8Encoding(false,true).GetString(bytes).TrimStart('\ufeff');}catch(DecoderFallbackException){text=Encoding.GetEncoding(932).GetString(bytes);}
        var comparer=Regex.IsMatch(text,@"(?im)^#BASE\s+62")?StringComparer.Ordinal:StringComparer.OrdinalIgnoreCase;
        var ends=Regex.Matches(text,@"(?im)^#LNOBJ\s+([0-9A-Z]{2})").Select(m=>m.Groups[1].Value).ToHashSet(comparer);
        var used=new HashSet<string>(comparer);
        foreach(Match match in Regex.Matches(text,@"(?im)^#\d{3}(01|[1-6][1-9]):\s*([0-9A-Z]+)"))
        {var data=match.Groups[2].Value;for(var i=0;i+1<data.Length;i+=2){var key=data.Substring(i,2);if(key!="00"&&!(match.Groups[1].Value[0] is '1' or '2'&&ends.Contains(key)))used.Add(key);}}
        var definitions=new Dictionary<string,string>(comparer);
        foreach(Match match in Regex.Matches(text,@"(?im)^#WAV([0-9A-Z]{2})[ \t]+([^\r\n]+)"))definitions[match.Groups[1].Value]=match.Groups[2].Value.Trim().Trim('"');
        var checkedFiles=0;
        foreach(var key in used)
        {
            // LR2 permits intentional silent notes with no WAV definition.
            if(!definitions.TryGetValue(key,out var relative))continue;
            var path=Transfer.Inside(root,Path.Combine(Path.GetDirectoryName(chart)!,relative.Replace('\\',Path.DirectorySeparatorChar)));
            if(!File.Exists(path)&&!new[]{".wav",".ogg",".mp3",".flac"}.Any(extension=>File.Exists(Path.ChangeExtension(path,extension))))throw new IOException("음원 파일을 찾을 수 없습니다: "+relative);
            checkedFiles++;
        }
        if(checkedFiles==0)throw new IOException("재생할 수 있는 음원이 없는 채보입니다.");
    }
    private long FreeBudget()
    {var available=new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace-512L*1024*1024;var budget=Math.Min(available,settings.LimitGiB*1024L*1024*1024-Size(cache));if(budget<1024*1024)throw new IOException("곡을 받을 저장 공간이 부족합니다.");return budget;}
    public long UsedBytes=>Size(cache);
    private static long Size(string root)=>Directory.Exists(root)?Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories).Sum(file=>new FileInfo(file).Length):0;
    public int Cleanup(long required=0,string? protect=null)
    {
        if(GameRunning())return 0;
        var receipts=Directory.EnumerateFiles(records,"*.json").Select(path=>JsonSerializer.Deserialize<InstallRecord>(File.ReadAllText(path))).Where(r=>r is not null).Cast<InstallRecord>().OrderBy(r=>r.Used).ToList();
        var total=Size(cache);var removed=0;
        foreach(var receipt in receipts)
        {
            if(receipt.Work==protect||GameRunning()&&receipt.Work==ProtectedWork)continue;
            if(receipt.Used>DateTimeOffset.UtcNow.AddDays(-settings.KeepDays)&&total+required<=settings.LimitGiB*1024L*1024*1024&&new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace>=required+512L*1024*1024)continue;
            folders.RemoveWork(receipt.Work);
            var path=Transfer.Inside(cache,receipt.Work);if(Directory.Exists(path)){var bytes=Size(path);Directory.Delete(path,true);total-=bytes;}
            File.Delete(RecordPath(receipt.Work));removed++;
        }
        return removed;
    }
    private void Report(string phase,string message)=>Changed?.Invoke(new(phase,message));
    public void Dispose()=>transfer.Dispose();
}
