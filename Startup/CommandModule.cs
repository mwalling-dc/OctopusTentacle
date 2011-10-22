using System;
using Autofac;

namespace Octopus.Shared.Startup
{
    public class CommandModule : Module
    {
        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);

            builder.RegisterCommand<HelpCommand>("help", "Prints this help text", "h", "?");
            builder.RegisterType<CommandProcessor>().As<ICommandProcessor>();
            builder.RegisterType<CommandLocator>().As<ICommandLocator>();
            builder.RegisterType<ServiceInstaller>().As<IServiceInstaller>();
        }
    }
}