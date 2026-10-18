using System.Windows;
namespace Depot.Desktop;
public partial class App:Application
{
    private Mutex? instance;
    private bool owned;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);instance=new Mutex(true,@"Local\BMSDepotNext.Library",out owned);
        if(!owned){MessageBox.Show("BMS Depot가 이미 실행 중입니다.","BMS Depot");Shutdown();return;}
        new Home().Show();
    }
    protected override void OnExit(ExitEventArgs e){if(owned)instance?.ReleaseMutex();instance?.Dispose();base.OnExit(e);}
}
