// Modified by SignalFx
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Permissions;
using System.Security.Policy;
using System.Threading;
using System.Web;
using AppDomain.Instance;

namespace SecurityGrant.FileNotFoundException
{
    public class Program
    {
        [LoaderOptimization(LoaderOptimization.MultiDomainHost)]
        static void Main(string[] args)
        {
            List<Thread> threads = new List<Thread>();
            int index = 0;

            PermissionSet ps = new PermissionSet(PermissionState.Unrestricted);
            System.AppDomain appDomain1 = CreateAndRunAppDomain(index++, ps);
            System.AppDomain appDomain2 = CreateAndRunAppDomain(index++, ps);

            Thread.Sleep(TimeSpan.FromSeconds(3));
        }

        private static System.AppDomain CreateAndRunAppDomain(int index, PermissionSet grantSet)
        {
            // Construct and initialize settings for a second AppDomain.
            AppDomainSetup ads = new AppDomainSetup();
            ads.ApplicationBase = System.AppDomain.CurrentDomain.BaseDirectory;

            ads.DisallowBindingRedirects = false;
            ads.DisallowCodeDownload = true;
            ads.ConfigurationFile =
                System.AppDomain.CurrentDomain.SetupInformation.ConfigurationFile;

            string name = "AppDomain" + index;
            System.AppDomain appDomain1 = System.AppDomain.CreateDomain(
                name,
                System.AppDomain.CurrentDomain.Evidence,
                ads,
                grantSet);

            var argsToPass = new string[] { name, index.ToString(), "SqlServer" };
            appDomain1.ExecuteAssemblyByName(
                typeof(AppDomainInstanceProgram).Assembly.FullName,
                argsToPass);

            Console.WriteLine("**********************************************");
            Console.WriteLine($"Finished executing in AppDomain {name}");
            Console.WriteLine("**********************************************");
            return appDomain1;
        }
    }
}
