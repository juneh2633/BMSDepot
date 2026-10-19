using System.Security.Cryptography;
using System.Text.Json;

namespace Depot.Engine;
public sealed record ExistingChart(string Root,string Path,string Md5,long Bytes,long Modified);
public sealed record ExistingIndex(List<string> Roots,List<ExistingChart> Charts);
public sealed record ScanResult(int Charts,int Matched,int Skipped);

// Read-only access to user-owned music. No cache cleanup or writes to source folders.
public sealed class ExistingLibrary
{
    private readonly string index;
    private ExistingIndex data;
    public IReadOnlyList<string> Roots=>data.Roots;
    public ExistingLibrary(string index)
    {
        this.index=index;
        data=File.Exists(index)?JsonSerializer.Deserialize<ExistingIndex>(File.ReadAllText(index))??new([],[]):new([],[]);
    }
    public HashSet<string> Hashes()=>data.Charts.Where(c=>File.Exists(c.Path)).Select(c=>c.Md5).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public string? PathFor(string md5)=>data.Charts.FirstOrDefault(c=>c.Md5==md5&&File.Exists(c.Path))?.Path;
    public async Task<ExistingChart?> Find(Song song,CancellationToken ct)
    {
        foreach(var chart in data.Charts.Where(c=>c.Md5==song.Md5))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var stream=File.OpenRead(chart.Path);
                if(Convert.ToHexStringLower(await MD5.HashDataAsync(stream,ct))!=song.Md5)continue;
                if(song.Sha256.Length>0){stream.Position=0;if(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,ct))!=song.Sha256)continue;}
                await Library.VerifyAudio(chart.Path,chart.Root,ct);return chart;
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
        }
        return null;
    }
    public async Task<ScanResult> Scan(string? addedRoot,Catalog catalog,IProgress<string> progress,CancellationToken ct)
    {
        var roots=data.Roots.ToList();
        if(addedRoot is not null)
        {
            var root=Path.GetFullPath(addedRoot);if(!Directory.Exists(root))throw new IOException("곡 폴더가 없습니다.");
            if(!roots.Contains(root,StringComparer.OrdinalIgnoreCase))roots.Add(root);
        }
        var known=data.Charts.GroupBy(c=>c.Path,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.OrdinalIgnoreCase);
        var next=new List<ExistingChart>();var skipped=0;var scanned=0;
        foreach(var root in roots)
        {
            var pending=new Stack<string>();pending.Push(root);var inaccessible=false;
            while(pending.Count>0)
            {
                ct.ThrowIfCancellationRequested();var folder=pending.Pop();
                if(folder.Equals(Preferences.Home,StringComparison.OrdinalIgnoreCase)||folder.StartsWith(Preferences.Home+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))continue;
                string[] files,children;
                try
                {
                    if((File.GetAttributes(folder)&FileAttributes.ReparsePoint)!=0){skipped++;continue;}
                    files=Directory.GetFiles(folder);children=Directory.GetDirectories(folder);
                }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){skipped++;inaccessible=true;continue;}
                foreach(var child in children)pending.Push(child);
                foreach(var path in files.Where(Transfer.IsChart))
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var info=new FileInfo(path);if((info.Attributes&FileAttributes.ReparsePoint)!=0){skipped++;continue;}
                        string hash;
                        if(known.TryGetValue(path,out var old)&&old.Bytes==info.Length&&old.Modified==info.LastWriteTimeUtc.Ticks)hash=old.Md5;
                        else{await using var stream=File.OpenRead(path);hash=Convert.ToHexStringLower(await MD5.HashDataAsync(stream,ct));}
                        next.Add(new(root,path,hash,info.Length,info.LastWriteTimeUtc.Ticks));scanned++;
                        if(scanned%100==0)progress.Report($"기존 곡을 확인합니다 · {scanned:N0}개 채보");
                    }
                    catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){skipped++;inaccessible=true;}
                }
            }
            if(inaccessible)next.AddRange(data.Charts.Where(c=>c.Root==root&&!next.Any(n=>n.Path.Equals(c.Path,StringComparison.OrdinalIgnoreCase))));
        }
        ct.ThrowIfCancellationRequested();
        var updated=new ExistingIndex(roots,next.DistinctBy(c=>c.Path,StringComparer.OrdinalIgnoreCase).ToList());
        Directory.CreateDirectory(Path.GetDirectoryName(index)!);var temp=index+".new";
        await File.WriteAllTextAsync(temp,JsonSerializer.Serialize(updated),ct);ct.ThrowIfCancellationRequested();File.Move(temp,index,true);data=updated;
        var ids=catalog.Songs.Select(s=>s.Md5).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new(scanned,data.Charts.Where(c=>ids.Contains(c.Md5)&&File.Exists(c.Path)).Select(c=>c.Md5).Distinct().Count(),skipped);
    }
}
