using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Archives.Zip;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;

namespace Depot.Engine;

public sealed class Transfer : IDisposable
{
    private readonly HttpClient http=new(){Timeout=TimeSpan.FromMinutes(30)};
    public Transfer()=>http.DefaultRequestHeaders.UserAgent.ParseAdd("BMSDepotNext/1.0");
    public async Task Download(DownloadSource source,string target,long limit,Action<long,long?> progress,CancellationToken ct)
    {
        var url=source.Url;
        if(source.FileId.Length>0)
        {
            using var request=new HttpRequestMessage(HttpMethod.Post,"https://horie.synology.me:8443/api/v1/files/"+Uri.EscapeDataString(source.FileId)+"/download-grants");
            request.Headers.Referrer=new Uri("https://horieyuuka.github.io/Songs");request.Headers.TryAddWithoutValidation("Origin","https://horieyuuka.github.io");
            using var response=await http.SendAsync(request,ct);response.EnsureSuccessStatusCode();
            using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            url=new Uri(new Uri("https://horie.synology.me:8443"),json.RootElement.GetProperty("downloadUrl").GetString()).ToString();
        }
        for(var depth=0;depth<4;depth++)
        {
            var uri=new Uri(url);
            if(uri.Scheme is not ("https" or "http"))throw new IOException("다운로드 주소 형식이 올바르지 않습니다.");
            if(uri.Host=="www.dropbox.com"||uri.Host=="dropbox.com")
            {var builder=new UriBuilder(uri){Query=string.Join('&',uri.Query.TrimStart('?').Split('&',StringSplitOptions.RemoveEmptyEntries).Where(p=>!p.StartsWith("dl=")&&!p.StartsWith("raw=")).Append("dl=1"))};uri=builder.Uri;}
            if(uri.Host=="drive.google.com"&&uri.AbsolutePath.StartsWith("/file/d/"))uri=new Uri("https://drive.google.com/uc?export=download&id="+uri.AbsolutePath.Split('/')[3]);
            using var response=await http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,ct);response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentType?.MediaType is "text/html" or "application/xhtml+xml")
            {
                if(response.Content.Headers.ContentLength>2*1024*1024)throw new IOException("파일 대신 큰 웹페이지가 반환됐습니다.");
                var html=await ReadBounded(response,2*1024*1024,ct);var page=response.RequestMessage!.RequestUri!;
                var forms=Regex.Matches(html,@"<form\b[^>]*>[\s\S]*?</form>",RegexOptions.IgnoreCase).Cast<Match>();
                var form=forms.FirstOrDefault(f=>Attribute(f.Value.Split('>')[0],"action").Contains("/download"));
                if(form is not null&&(page.Host=="drive.google.com"||page.Host=="drive.usercontent.google.com"))
                {
                    var action=new Uri(page,Attribute(form.Value.Split('>')[0],"action"));
                    var fields=Regex.Matches(form.Value,@"<input\b[^>]*>",RegexOptions.IgnoreCase).Cast<Match>()
                        .Where(m=>Attribute(m.Value,"name").Length>0).Select(m=>Uri.EscapeDataString(Attribute(m.Value,"name"))+"="+Uri.EscapeDataString(Attribute(m.Value,"value")));
                    url=action+(action.Query.Length>0?"&":"?")+string.Join('&',fields);continue;
                }
                var links=Regex.Matches(html,"href\\s*=\\s*[\"']([^\"']+)[\"']",RegexOptions.IgnoreCase).Cast<Match>()
                    .Select(m=>Uri.TryCreate(page,WebUtility.HtmlDecode(m.Groups[1].Value),out var link)?link:null)
                    .Where(link=>link is not null&&link.Scheme is "http" or "https"&&(Regex.IsMatch(link.AbsolutePath,@"\.(zip|rar|7z)$",RegexOptions.IgnoreCase)||link.Host=="drive.google.com"&&link.AbsolutePath.StartsWith("/file/d/")))
                    .Distinct().ToList();
                if(links.Count!=1)throw new IOException("배포 페이지에서 원곡 파일 하나를 특정하지 못했습니다.");
                url=links[0]!.ToString();continue;
            }
            var total=response.Content.Headers.ContentLength;if(total>limit||source.Bytes>limit)throw new IOException("다운로드할 공간이 부족합니다.");
            await using var input=await response.Content.ReadAsStreamAsync(ct);await using var output=File.Create(target);
            var buffer=new byte[131072];int read;long count=0;var last=Environment.TickCount64;
            while((read=await input.ReadAsync(buffer,ct))>0){count+=read;if(count>limit)throw new IOException("저장 한도를 초과했습니다.");await output.WriteAsync(buffer.AsMemory(0,read),ct);if(Environment.TickCount64-last>=100){progress(count,total);last=Environment.TickCount64;}}
            progress(count,total);
            if(count==0||(total.HasValue&&count!=total)||(source.Bytes>0&&count!=source.Bytes))throw new IOException("다운로드가 완료되지 않았습니다.");
            return;
        }
        throw new IOException("배포처의 확인 페이지를 통과하지 못했습니다.");
    }
    public async Task<List<DownloadSource>> Find(Song song,CancellationToken ct)
    {
        var result=new List<DownloadSource>();
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var title=Regex.Replace(Regex.Replace(song.Title,@"\s*\[[^\]]*\]\s*$",""),@"\s+[-~].+[-~]$","").Trim();
        using var response=await http.GetAsync("https://horie.synology.me:8443/api/v1/folders/Songs/files?limit=5&offset=0&q="+Uri.EscapeDataString(title),HttpCompletionOption.ResponseHeadersRead,timeout.Token);
        response.EnsureSuccessStatusCode();using var json=JsonDocument.Parse(await ReadBounded(response,2*1024*1024,timeout.Token));
        var root=json.RootElement;if(!root.TryGetProperty("items",out var items)&&!root.TryGetProperty("files",out items))return result;
        var exact=items.EnumerateArray().Where(item=>Normalize(Regex.Replace(Path.GetFileNameWithoutExtension(item.GetProperty("name").GetString()??""),@"^\[[^\]]+\]\s*",""))==Normalize(title)).ToList();
        if(exact.Count==1){var item=exact[0];result.Add(new("Horie", "",item.GetProperty("id").GetString()!,item.GetProperty("sizeBytes").GetInt64()));}
        return result;
    }
    private static string Attribute(string tag,string name)=>WebUtility.HtmlDecode(Regex.Match(tag,@"\b"+name+"\\s*=\\s*[\"']([^\"']*)[\"']",RegexOptions.IgnoreCase).Groups[1].Value);
    public static string Normalize(string text)=>string.Concat(text.Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToLowerInvariant();
    private static async Task<string> ReadBounded(HttpResponseMessage response,int limit,CancellationToken ct)
    {await using var stream=await response.Content.ReadAsStreamAsync(ct);using var data=new MemoryStream();var buffer=new byte[16384];int read;while((read=await stream.ReadAsync(buffer,ct))>0){if(data.Length+read>limit)throw new IOException("응답 크기 초과");data.Write(buffer,0,read);}return Encoding.UTF8.GetString(data.ToArray());}
    public static bool IsChart(string path)=>Path.GetExtension(path).ToLowerInvariant() is ".bms" or ".bme" or ".bml" or ".pms";
    public static string Inside(string root,string path)
    {var full=Path.GetFullPath(Path.Combine(root,path));if(!full.StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("곡 폴더를 벗어나는 경로입니다.");return full;}
    public static void Extract(string source,string root,long limit,CancellationToken ct)
    {
        using var file=File.OpenRead(source);Span<byte> signature=stackalloc byte[8];file.ReadExactly(signature);file.Position=0;
        using IArchive archive=signature[0]==0x50?ZipArchive.OpenArchive(file):signature[0]==0x52?RarArchive.OpenArchive(file):signature[0]==0x37?SevenZipArchive.OpenArchive(file):throw new IOException("지원하는 압축파일이 아닙니다.");
        long total=0;var entries=0;var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Write(IEntry entry,Func<Stream> open)
        {
            ct.ThrowIfCancellationRequested();if(entry.IsDirectory)return;
            if(++entries>100000||entry.IsEncrypted||entry.LinkTarget is not null)throw new IOException("지원하지 않는 압축 항목입니다.");
            var relative=(entry.Key??"").Replace('\\','/');var path=Inside(root,relative);
            if(!names.Add(path))throw new IOException("중복 압축 경로입니다.");
            if(Path.GetExtension(path).ToLowerInvariant() is not (".bms" or ".bme" or ".bml" or ".pms" or ".wav" or ".ogg" or ".mp3" or ".flac" or ".bmp" or ".png" or ".jpg" or ".jpeg" or ".mpg" or ".mpeg" or ".avi" or ".mp4" or ".wmv" or ".txt"))return;
            if(entry.Size<0||entry.Size>limit-total)throw new IOException("압축 해제 용량 한도 초과");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var input=open();using var output=File.Create(path);
            var buffer=new byte[131072];int read;long bytes=0;
            while((read=input.Read(buffer,0,buffer.Length))>0){ct.ThrowIfCancellationRequested();bytes+=read;total+=read;if(total>limit||bytes>entry.Size)throw new IOException("압축 크기 불일치");output.Write(buffer,0,read);}
            if(bytes!=entry.Size)throw new IOException("압축 해제가 완료되지 않았습니다.");
        }
        if(archive.IsSolid||archive.Type==ArchiveType.SevenZip){using var reader=archive.ExtractAllEntries();while(reader.MoveToNextEntry())Write(reader.Entry,reader.OpenEntryStream);}
        else foreach(var entry in archive.Entries)Write(entry,entry.OpenEntryStream);
    }
    public void Dispose()=>http.Dispose();
}
