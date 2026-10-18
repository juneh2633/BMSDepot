using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Depot.Engine;

public sealed record FolderEntry(string Work,string Chart,string Table,string Level,string Title,string RelativeChart);

// Each view contains one unchanged chart and hard links to shared package assets.
public sealed class ChartFolders
{
    public string Root {get;}
    private readonly string cache,index,temporary;
    public Func<bool> GameRunning {get;set;}=()=>false;
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool CreateHardLink(string target,string source,IntPtr reserved);
    public ChartFolders(string libraryRoot,string cache)
    {
        this.cache=cache;Root=Path.Combine(libraryRoot,"난이도표");index=Path.Combine(libraryRoot,"folder-index.json");temporary=Path.Combine(libraryRoot,"folder-staging");
        Directory.CreateDirectory(Root);Directory.CreateDirectory(temporary);
    }
    private List<FolderEntry> Read()=>File.Exists(index)?JsonSerializer.Deserialize<List<FolderEntry>>(File.ReadAllText(index))??[]:[];
    private void Save(List<FolderEntry> entries)
    {File.WriteAllText(index+".new",JsonSerializer.Serialize(entries));File.Move(index+".new",index,true);}
    private static string Name(string value)
    {
        var clean=new string(value.Select(c=>Path.GetInvalidFileNameChars().Contains(c)||char.IsControl(c)?'_':c).ToArray()).Trim().TrimEnd('.');
        if(clean.Length>48)clean=clean[..48];
        var reserved=new[]{"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","LPT1","LPT2","LPT3"};
        if(clean.Length==0||clean!=value||reserved.Contains(clean.Split('.')[0],StringComparer.OrdinalIgnoreCase))
            clean=(clean.Length==0?"기타":clean)+"_"+Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];
        return clean;
    }
    private string Relative(FolderEntry entry)
    {
        var level=entry.Level;
        if(int.TryParse(level,out _))level=(entry.Table=="Satellite"?"sl":entry.Table=="Stella"?"st":"레벨 ")+level;
        var title=entry.Title.Length>24?entry.Title[..24]:entry.Title;
        return Path.Combine(Name(entry.Table),Name(level),Name(title)+" ["+entry.Chart+"]");
    }
    public void Publish(Song song,string chart,string? table,CancellationToken ct)
    {
        var workRoot=Transfer.Inside(cache,song.Work);
        var relative=Path.GetRelativePath(workRoot,Transfer.Inside(workRoot,chart));
        var entries=Read();
        var memberships=song.Tables.Where(t=>table is null||t.Table==table).Distinct().ToList();
        if(memberships.Count==0)memberships.Add(new("기타","미분류"));
        foreach(var membership in memberships)
        {
            var added=new FolderEntry(song.Work,song.Md5,membership.Table,membership.Level,song.Title,relative);
            entries.RemoveAll(e=>e.Chart==added.Chart&&e.Table==added.Table&&e.Level==added.Level);entries.Add(added);
        }
        // Re-link other published charts too if the package has been replaced.
        Save(entries);
        foreach(var entry in entries.Where(e=>e.Work==song.Work))Build(entry,ct);
    }
    private void Build(FolderEntry entry,CancellationToken ct)
    {
        var source=Transfer.Inside(cache,entry.Work);var chart=Transfer.Inside(source,entry.RelativeChart);
        if(!File.Exists(chart))throw new IOException("폴더에 연결할 실제 채보가 없습니다.");
        var destination=Transfer.Inside(Root,Relative(entry));
        var stage=Transfer.Inside(temporary,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        var backup=stage+".previous";
        try
        {
            foreach(var file in Directory.EnumerateFiles(source,"*",SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if(Transfer.IsChart(file)&&!string.Equals(file,chart,StringComparison.OrdinalIgnoreCase))continue;
                var target=Transfer.Inside(stage,Path.GetRelativePath(source,file));Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if(!CreateHardLink(target,file,IntPtr.Zero))throw new IOException("난이도 폴더 연결에 실패했습니다. 같은 NTFS 드라이브와 충분한 여유 공간이 필요합니다. Windows 오류: "+Marshal.GetLastWin32Error());
            }
            ct.ThrowIfCancellationRequested();Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if(Directory.Exists(destination))
            {
                if(GameRunning())return;
                Directory.Move(destination,backup);
            }
            try{Directory.Move(stage,destination);}
            catch{if(Directory.Exists(backup)&&!Directory.Exists(destination))Directory.Move(backup,destination);throw;}
            if(Directory.Exists(backup))Directory.Delete(backup,true);
        }
        finally
        {
            if(Directory.Exists(stage))try{Directory.Delete(stage,true);}catch(IOException){}
        }
    }
    public void RemoveWork(string work)
    {
        var entries=Read();
        foreach(var entry in entries.Where(e=>e.Work==work))
        {var path=Transfer.Inside(Root,Relative(entry));if(Directory.Exists(path))Directory.Delete(path,true);}
        entries.RemoveAll(e=>e.Work==work);Save(entries);
    }
}
