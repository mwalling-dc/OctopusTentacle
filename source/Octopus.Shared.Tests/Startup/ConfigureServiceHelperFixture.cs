using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.ServiceProcess;
using NSubstitute;
using NUnit.Framework;
using Octopus.Configuration;
using Octopus.Shared.Configuration;
using Octopus.Shared.Diagnostics;
using Octopus.Shared.Internals.Options;
using Octopus.Shared.Startup;
using Octopus.Shared.Tests.Support;
using Octopus.Shared.Tests.Util;
using Octopus.Shared.Util;

namespace Octopus.Shared.Tests
{
    [TestFixture]
    public class ConfigureServiceHelperFixture
    {
        [Test]
        public void CanInstallService()
        {
            if(!PlatformDetection.IsRunningOnWindows)
                Assert.Inconclusive("This test is only supported on windows.");
        
            const string serviceName = "OctopusShared.ServiceHelperTest";
            const string instance = "TestInstance";
            const string serviceDescription = "Test service for OctopusShared tests";
            var log = new InMemoryLog();
            var root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().FullLocalPath());
            var exePath = Path.Combine(root, "Startup\\Packages\\Acme.Service", "Acme.Service.exe");
            
            DeleteExistingService(serviceName);

            var user = new TestUserPrincipal("octo-shared-svc-test");
            var serviceConfigurationState = new ServiceConfigurationState
            {
                Install = true,
                Password = user.Password,
                Username = user.NTAccountName,
                Start = true
            };
            var configureServiceHelper = new ConfigureServiceHelper(log, serviceName, exePath, instance, serviceDescription, serviceConfigurationState);
            
            try
            {
                configureServiceHelper.ConfigureService();
                
                using (var installedService = GetInstalledService(serviceName))
                {
                    Assert.NotNull(installedService, "Service is installed");
                    Assert.AreEqual(ServiceControllerStatus.Running, installedService.Status);
                }
            }
            finally
            {
                user?.Delete();
                DeleteExistingService(serviceName);
            }
        }
        
        ServiceController GetInstalledService(string serviceName)
        {
            return ServiceController.GetServices().FirstOrDefault(s => s.ServiceName == serviceName);
        }
        
        void DeleteExistingService(string serviceName)
        {
            var service = GetInstalledService(serviceName);
            if (service != null)
            {
                var system32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
                var sc = Path.Combine(system32, "sc.exe");

                Process.Start(new ProcessStartInfo(sc, $"stop {serviceName}") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
                Process.Start(new ProcessStartInfo(sc, $"delete {serviceName}") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit();
            }
        }
    }
}
