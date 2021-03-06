using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Aggregator.DI;
using Aggregator.Event;
using Aggregator.Exceptions;
using Aggregator.Internal;
using Aggregator.Persistence;

namespace Aggregator.Command
{
    /// <summary>
    /// This class is responsible for processing commands where the aggregate root identifier is a <see cref="string"/> and commands/events derive from <see cref="object"/>.
    /// Should be used as a singleton.
    /// </summary>
    public class CommandProcessor : CommandProcessor<string, object, object>, ICommandProcessor
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="CommandProcessor"/> class.
        /// </summary>
        /// <param name="serviceScopeFactory">The service scope factory.</param>
        /// <param name="eventStore">The event store.</param>
        /// <param name="eventDispatcher">The event dispatcher.</param>
        /// <param name="notificationHandlers">Optional <see cref="CommandProcessorNotificationHandlers"/> instance.</param>
        public CommandProcessor(
            IServiceScopeFactory serviceScopeFactory,
            IEventStore<string, object> eventStore,
            IEventDispatcher<object> eventDispatcher,
            CommandProcessorNotificationHandlers notificationHandlers = null)
            : base(serviceScopeFactory, eventStore, eventDispatcher, notificationHandlers)
        {
        }
    }

    /// <summary>
    /// This class is responsibly for processing commands.
    /// Should be used as a singleton.
    /// </summary>
    /// <typeparam name="TIdentifier">The identifier type.</typeparam>
    /// <typeparam name="TCommandBase">The command base type.</typeparam>
    /// <typeparam name="TEventBase">The event base type.</typeparam>
    public class CommandProcessor<TIdentifier, TCommandBase, TEventBase> : ICommandProcessor<TCommandBase>
        where TIdentifier : IEquatable<TIdentifier>
    {
        private readonly ConcurrentDictionary<Type, MethodInfo> _executeMethodCache = new ConcurrentDictionary<Type, MethodInfo>();
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly IEventStore<TIdentifier, TEventBase> _eventStore;
        private readonly IEventDispatcher<TEventBase> _eventDispatcher;
        private readonly CommandProcessorNotificationHandlers<TIdentifier, TCommandBase, TEventBase> _notificationHandlers;

        /// <summary>
        /// Initializes a new instance of the <see cref="CommandProcessor{TIdentifier, TCommandBase, TEventBase}"/> class.
        /// </summary>
        /// <param name="serviceScopeFactory">The service scope factory.</param>
        /// <param name="eventStore">The event store.</param>
        /// <param name="eventDispatcher">The event dispatcher.</param>
        /// <param name="notificationHandlers">Optional <see cref="CommandProcessorNotificationHandlers{TIdentifier, TCommandBase, TEventBase}"/> instance.</param>
        public CommandProcessor(
            IServiceScopeFactory serviceScopeFactory,
            IEventStore<TIdentifier, TEventBase> eventStore,
            IEventDispatcher<TEventBase> eventDispatcher,
            CommandProcessorNotificationHandlers<TIdentifier, TCommandBase, TEventBase> notificationHandlers = null)
        {
            _serviceScopeFactory = serviceScopeFactory ?? throw new ArgumentNullException(nameof(serviceScopeFactory));
            _eventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
            _eventDispatcher = eventDispatcher ?? throw new ArgumentNullException(nameof(eventDispatcher));
            _notificationHandlers = notificationHandlers ?? new CommandProcessorNotificationHandlers<TIdentifier, TCommandBase, TEventBase>();
        }

        /// <inheritdoc/>
        public async Task Process(TCommandBase command, CancellationToken cancellationToken = default)
        {
            if (command == null) throw new ArgumentNullException(nameof(command));

            using (IServiceScope serviceScope = _serviceScopeFactory.CreateScope())
            {
                CommandHandlingContext context = serviceScope.GetService<CommandHandlingContext>();
                _notificationHandlers.OnPrepareContext(command, context);

                UnitOfWork<TIdentifier, TEventBase> unitOfWork = context.CreateUnitOfWork<TIdentifier, TEventBase>();

                MethodInfo executeMethod =
                    _executeMethodCache.GetOrAdd(command.GetType(), type =>
                        typeof(CommandProcessor<TIdentifier, TCommandBase, TEventBase>)
                            .GetMethod(nameof(Execute), BindingFlags.NonPublic | BindingFlags.Instance)
                            ?.MakeGenericMethod(type))
                    ?? throw new InvalidOperationException($"Couldn't make generic {nameof(Execute)} method");

                await ((Task)executeMethod.Invoke(this, new object[] { command, serviceScope, cancellationToken })).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (!unitOfWork.HasChanges)
                {
                    return;
                }

                using (IEventStoreTransaction<TIdentifier, TEventBase> transaction = _eventStore.BeginTransaction(context))
                {
                    var storedEvents = new List<TEventBase>();

                    try
                    {
                        foreach (AggregateRootEntity<TIdentifier, TEventBase> aggregateRootEntity in unitOfWork.GetChanges())
                        {
                            TEventBase[] events = aggregateRootEntity.GetChanges();
                            events = events.Select(x => _notificationHandlers.OnEnrichEvent(x, command, context)).ToArray();
                            await transaction.StoreEvents(aggregateRootEntity.Identifier, aggregateRootEntity.ExpectedVersion, events, cancellationToken).ConfigureAwait(false);
                            storedEvents.AddRange(events);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        await _eventDispatcher.Dispatch(storedEvents.ToArray(), cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();

                        await transaction.Commit().ConfigureAwait(false);
                    }
                    catch
                    {
                        await transaction.Rollback().ConfigureAwait(false);
                        throw;
                    }
                }
            }
        }

        private async Task Execute<TCommand>(TCommand command, IServiceScope serviceScope, CancellationToken cancellationToken)
            where TCommand : TCommandBase
        {
            ICommandHandler<TCommand>[] handlers = serviceScope.GetServices<ICommandHandler<TCommand>>()?.ToArray();

            if (handlers == null || !handlers.Any())
                throw new UnhandledCommandException(command);

            foreach (ICommandHandler<TCommand> handler in handlers)
                await handler.Handle(command, cancellationToken).ConfigureAwait(false);
        }
    }
}
