using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newbe.Claptrap.StorageProvider.PostgreSQL.Options;
using Newbe.Claptrap.StorageProvider.Relational.EventStore;
using Newbe.Claptrap.StorageProvider.Relational.Extensions;
using Npgsql;
using NpgsqlTypes;

namespace Newbe.Claptrap.StorageProvider.PostgreSQL.EventStore
{
    public class PostgreSQLEventEntitySaver : IEventEntitySaver<EventEntity>
    {
        private readonly IBatchOperator<EventEntity> _batchOperator;
        private readonly string _connectionName;
        private readonly string _schemaName;
        private readonly string _eventTableName;

        public PostgreSQLEventEntitySaver(
            BatchOperator<EventEntity>.Factory batchOperatorFactory,
            IMasterOrSelfIdentity identity,
            IDbFactory dbFactory,
            IPostgreSQLEventStoreOptions options,
            IBatchOperatorContainer batchOperatorContainer)
        {
            var (connectionName, schemaName, eventTableName) =
                options.RelationalEventStoreLocator.GetNames(identity.Identity);
            _connectionName = connectionName;
            _schemaName = schemaName;
            _eventTableName = eventTableName;
            var operatorKey = new BatchOperatorKey()
                .With(nameof(PostgreSQLEventEntitySaver))
                .With(_connectionName)
                .With(_schemaName)
                .With(_eventTableName);

            _batchOperator = (IBatchOperator<EventEntity>) batchOperatorContainer.GetOrAdd(
                operatorKey, () => batchOperatorFactory.Invoke(
                    new BatchOperatorOptions<EventEntity>(options)
                    {
                        DoManyFunc = (entities, cacheData) => SaveManyCoreMany(dbFactory, entities)
                    }));
        }

        public Task SaveAsync(EventEntity entity)
        {
            return _batchOperator.CreateTask(entity);
        }

        private async Task SaveManyCoreMany(
            IDbFactory factory,
            IEnumerable<EventEntity> entities)
        {
            var array = entities as EventEntity[] ?? entities.ToArray();
            var items = array
                .Select(x => new RelationalEventEntity
                {
                    claptrap_id = x.ClaptrapId,
                    claptrap_type_code = x.ClaptrapTypeCode,
                    created_time = x.CreatedTime,
                    event_data = x.EventData,
                    event_type_code = x.EventTypeCode,
                    version = x.Version
                })
                .ToArray();

            await using var db = (NpgsqlConnection) factory.GetConnection(_connectionName);
            await db.OpenAsync();
            await using var importer =
                db.BeginBinaryImport(
                    $"COPY {_schemaName}.{_eventTableName} (claptrap_type_code, claptrap_id, version, event_type_code, event_data, created_time) FROM STDIN (FORMAT BINARY)");
            foreach (var entity in items)
            {
                await importer.StartRowAsync();
                await importer.WriteAsync(entity.claptrap_type_code);
                await importer.WriteAsync(entity.claptrap_id);
                await importer.WriteAsync(entity.version, NpgsqlDbType.Bigint);
                await importer.WriteAsync(entity.event_type_code);
                await importer.WriteAsync(entity.event_data);
                await importer.WriteAsync(entity.created_time, NpgsqlDbType.Date);
            }

            importer.Complete();
        }
    }
}