using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace Depot.Install;

static class Program
{
    [STAThread]static void Main(string[] args)
    {
        if(args.Length==2&&args[0]=="--check-install"){Extract(Path.GetFullPath(args[1]));return;}
        ApplicationConfiguration.Initialize();Application.Run(new Installer());
    }
    public static void Extract(string root)
    {
        Directory.CreateDirectory(root);var prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        using var payload=Assembly.GetExecutingAssembly().GetManifestResourceStream("package.zip")??throw new IOException("설치 데이터가 없습니다.");using var zip=new ZipArchive(payload);
        var files=new List<string>();
        foreach(var entry in zip.Entries)
        {
            if(entry.FullName.EndsWith('/'))continue;
            var path=Path.GetFullPath(Path.Combine(root,entry.FullName));if(!path.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new IOException("설치 경로 오류");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);using(var input=entry.Open())using(var output=File.Create(path))input.CopyTo(output);files.Add(entry.FullName);
        }
        File.WriteAllLines(Path.Combine(root,"installed-files.txt"),files.Append("Uninstall.ps1").Append("installed-files.txt"));
    }
}
sealed class Installer:Form
{
    private readonly Button install=new(){Text="설치하고 시작",Width=400,Height=52};
    private readonly Label status=new(){Text="Depot에서 다운로드하고, 일반 OpenLR2에서 F8로 곡 목록을 갱신합니다.",Width=440,Height=60};
    public Installer()
    {
        Text="BMS Depot 새 버전 설치";ClientSize=new Size(500,260);StartPosition=FormStartPosition.CenterScreen;MaximizeBox=false;FormBorderStyle=FormBorderStyle.FixedDialog;
        var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(26),FlowDirection=FlowDirection.TopDown,WrapContents=false};
        panel.Controls.Add(new Label{Text="BMS Depot",AutoSize=true,Font=new Font(Font.FontFamily,24,FontStyle.Bold)});panel.Controls.Add(status);panel.Controls.Add(install);Controls.Add(panel);install.Click+=Install;
    }
    private async void Install(object? sender,EventArgs e)
    {
        install.Enabled=false;
        try
        {
            foreach(var p in Process.GetProcessesByName("BMSDepotNext"))using(p)if(!p.HasExited)throw new IOException("실행 중인 BMS Depot 새 버전을 종료하세요.");
            var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","BMSDepotNext");
            status.Text="프로그램을 설치하고 있습니다…";await Task.Run(()=>Program.Extract(root));
            File.WriteAllText(Path.Combine(root,"Uninstall.ps1"),Uninstaller,new UTF8Encoding(true));
            using(var key=Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\BMSDepotNext"))
            {
                key.SetValue("DisplayName","BMS Depot");key.SetValue("DisplayVersion",Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]??"0.1.0-alpha.1");key.SetValue("InstallLocation",root);
                key.SetValue("UninstallString","powershell.exe -NoProfile -ExecutionPolicy Bypass -File \""+Path.Combine(root,"Uninstall.ps1")+"\"");key.SetValue("NoRepair",1);key.SetValue("NoModify",1);
            }
            var type=Type.GetTypeFromProgID("WScript.Shell")!;dynamic shell=Activator.CreateInstance(type)!;
            dynamic link=shell.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"BMS Depot 새 버전.lnk"));link.TargetPath=Path.Combine(root,"BMSDepotNext.exe");link.WorkingDirectory=root;link.Save();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
            Process.Start(new ProcessStartInfo(Path.Combine(root,"BMSDepotNext.exe")){UseShellExecute=true,WorkingDirectory=root});Close();
        }
        catch(Exception ex){status.Text=ex.Message;install.Enabled=true;}
    }
    private const string Uninstaller="""
    $ErrorActionPreference = 'Stop'
    Add-Type -AssemblyName System.Windows.Forms
    if ([System.Windows.Forms.MessageBox]::Show('프로그램을 제거할까요? 곡과 플레이 기록은 유지됩니다.', 'BMS Depot 제거', 'YesNo') -ne 'Yes') { exit }
    if (Get-Process -Name BMSDepotNext,OpenLR2_Next -ErrorAction SilentlyContinue) { [System.Windows.Forms.MessageBox]::Show('앱과 게임을 먼저 종료하세요.'); exit }
    $installRoot = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $prefix = $installRoot + [IO.Path]::DirectorySeparatorChar
    $owned = @(Get-Content -LiteralPath (Join-Path $installRoot 'installed-files.txt'))
    foreach ($relative in $owned) {
      $target = [IO.Path]::GetFullPath((Join-Path $installRoot $relative))
      if (-not $target.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw '제거 경로 오류' }
      if (Test-Path -LiteralPath $target -PathType Leaf) { Remove-Item -LiteralPath $target -Force }
    }
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'BMS Depot 새 버전.lnk'
    if (Test-Path -LiteralPath $shortcut) { Remove-Item -LiteralPath $shortcut -Force }
    Remove-Item -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\BMSDepotNext' -ErrorAction SilentlyContinue
    """;
}
