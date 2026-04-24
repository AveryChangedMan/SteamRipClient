using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using SteamRipApp.Core;
namespace SteamRipApp
{
    public static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--worker")
            {
                NativeBridgeService.Start();
                new ManualResetEvent(false).WaitOne();
                return;
            }
            WinRT.ComWrappersSupport.InitializeComWrappers();
            var instance = AppInstance.FindOrRegisterForKey("SteamRipAppMainInstance");
            if (instance.IsCurrent)
            {
                Application.Start((p) =>
                {
                    var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    new App();
                });
            }
            else
            {
                instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs()).AsTask().Wait();
            }
        }
    }
}
