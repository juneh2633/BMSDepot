using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Depot.Desktop;
public partial class Welcome:Window
{
    private int step;
    private readonly string folder;
    public string? ExistingRoot {get;private set;}
    public bool FolderRegistered=>Registered.IsChecked==true;
    public Welcome(string folder,bool registered,bool hasExisting,int firstStep=0)
    {
        InitializeComponent();this.folder=folder;FolderPath.Text=folder;Registered.IsChecked=registered;step=firstStep;
        if(hasExisting)ExistingPath.Text="이미 등록한 기존 곡 폴더가 있습니다. 그대로 사용하거나 다른 폴더를 더 고를 수 있습니다.";
        Draw();
    }
    private void Draw()
    {
        StepLabel.Text=$"시작 안내 · {step+1} / 3";
        Heading.Text=new[]{"이미 가지고 있는 곡이 있나요?","다운로드 폴더를 게임에 알려주세요","이제 곡을 받아 플레이하세요"}[step];
        ExistingStep.Visibility=step==0?Visibility.Visible:Visibility.Collapsed;FolderStep.Visibility=step==1?Visibility.Visible:Visibility.Collapsed;PlayStep.Visibility=step==2?Visibility.Visible:Visibility.Collapsed;
        BackButton.IsEnabled=step>0;NextButton.Content=step==2?"곡 찾아보기":"다음";
        FinishHint.Text=FolderRegistered?"설정이 끝났습니다. 안내는 메인 화면의 사용 방법에서 다시 볼 수 있습니다.":"폴더 등록을 아직 하지 않았다면 메인 화면의 게임에 폴더 등록에서 다시 확인할 수 있습니다.";
    }
    private void Next(object sender,RoutedEventArgs e){if(step<2){step++;Draw();}else{DialogResult=true;}}
    private void Back(object sender,RoutedEventArgs e){if(step>0){step--;Draw();}}
    private void ChooseExisting(object sender,RoutedEventArgs e)
    {var dialog=new OpenFolderDialog{Title="사용하던 BMS 곡 폴더 선택"};if(dialog.ShowDialog(this)==true){ExistingRoot=dialog.FolderName;ExistingPath.Text=ExistingRoot;}}
    private void CopyPath(object sender,RoutedEventArgs e){Clipboard.SetText(folder);CopyHint.Text="복사했습니다. 게임 설정의 곡 폴더 추가에 붙여넣으세요. 이 안내창이 게임 설정을 대신 바꾸지는 않습니다.";}
    private void OpenFolder(object sender,RoutedEventArgs e){Directory.CreateDirectory(folder);Process.Start(new ProcessStartInfo("explorer.exe",folder){UseShellExecute=true});}
}
