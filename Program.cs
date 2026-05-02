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

            WinRT.ComWrappersSupport.InitializeComWrappers();

            if (!Microsoft.Windows.ApplicationModel.DynamicDependency.Bootstrap.TryInitialize(0x00010005, out int hr))
            {

            }

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