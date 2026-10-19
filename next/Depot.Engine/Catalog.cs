using System.Text.Json;

namespace Depot.Engine;

public sealed record DownloadSource(string Label,string Url,string FileId,long Bytes);
public sealed record TableLevel(string Table,string Level);
public sealed record Song(string Id,string Work,string Title,string Artist,string Md5,string Sha256,List<TableLevel> Tables,List<DownloadSource> Sources);
public sealed record Catalog(int Version,List<Song> Songs)
{
    public static async Task<Catalog> Load(string file,CancellationToken ct=default)
    {
        await using var input=File.OpenRead(file);
        var catalog=await JsonSerializer.DeserializeAsync<Catalog>(input,cancellationToken:ct)??throw new IOException("곡 목록을 읽지 못했습니다.");
        if(catalog.Version!=1||catalog.Songs.Any(s=>s.Id!=s.Md5||s.Md5.Length!=32||!s.Md5.All(char.IsAsciiHexDigit)))throw new IOException("잘못된 곡 목록입니다.");
        return catalog;
    }
}
public sealed class Preferences
{
    public string PlayerFolder {get;set;}="";
    public string Table {get;set;}="Satellite";
    public int KeepDays {get;set;}=14;
    public int LimitGiB {get;set;}=20;
    public bool SetupComplete {get;set;}
    public bool FolderRegistered {get;set;}
    public static string Home=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BMSDepotNext");
    public static string FilePath=>Path.Combine(Home,"settings.json");
    public static Preferences Load()=>File.Exists(FilePath)?JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath))??new():new();
    public void Save()
    {
        if(KeepDays is <1 or >3650||LimitGiB is <1 or >200)throw new IOException("보관 기간은 1~3650일, 저장 용량은 1~200 GiB입니다.");
        Directory.CreateDirectory(Home);File.WriteAllText(FilePath+".new",JsonSerializer.Serialize(this));File.Move(FilePath+".new",FilePath,true);
    }
}
