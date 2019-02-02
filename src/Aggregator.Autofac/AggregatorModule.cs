using System;
using System.Diagnostics.CodeAnalysis;
using Aggregator.Command;
using Aggregator.DI;
using Aggregator.Persistence;
using Autofac;

namespace Aggregator.Autofac
{
    /// <summary>
    /// Autofac module for the Aggregator library where aggregate root identifiers are of type <see cref="string"/>, commands implement <see cref="ICommand"/> and events implement <see cref="IEvent"/>.
    /// </summary>
    [ExcludeFromCodeCoverage]
    public class AggregatorModule : AggregatorModule<string, ICommand, IEvent>
    {
        /// <summary>
        /// Adds Aggregator related registrations to the container.
        /// </summary>
        /// <param name="builder">The container builder.</param>
        protected override void Load(ContainerBuilder builder)
        {
            base.Load(builder);

            // Registrate the non-generic overrides on top of the generic base stuff
            builder.RegisterType<CommandProcessor>().As<ICommandProcessor>().SingleInstance();
            builder.RegisterGeneric(typeof(Repository<>)).As(typeof(IRepository<>)).InstancePerLifetimeScope();
        }
    }

    /// <summary>
    /// Autofac module for the Aggregator library.
    /// </summary>
    /// <typeparam name="TIdentifier">The identifier type.</typeparam>
    /// <typeparam name="TCommandBase">The command base type.</typeparam>
    /// <typeparam name="TEventBase">The event base type.</typeparam>
    [ExcludeFromCodeCoverage]
    public class AggregatorModule<TIdentifier, TCommandBase, TEventBase> : Module
        where TIdentifier : IEquatable<TIdentifier>
        where TCommandBase : ICommand
        where TEventBase : IEvent
    {
    /// <summary>
    /// Adds Aggregator related registrations to the container.
    /// </summary>
    /// <param name="builder">The container builder.</param>
    protected override void Load(ContainerBuilder builder)
    {
        builder.RegisterType<CommandHandlingContext>().AsSelf().InstancePerLifetimeScope();
        builder.RegisterType<CommandProcessor<TIdentifier, TCommandBase, TEventBase>>()
            .As<ICommandProcessor<TCommandBase>>().SingleInstance();
        builder.RegisterGeneric(typeof(Repository<,,>)).As(typeof(IRepository<,,>)).InstancePerLifetimeScope();
        builder.RegisterType<ServiceScopeFactory>().As<IServiceScopeFactory>().SingleInstance();
    }
    }
}
