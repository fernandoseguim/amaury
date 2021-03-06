using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amaury.Abstractions;
using Amaury.Persistence;
using Amaury.Store.DynamoDb.Configurations;
using Amaury.Store.DynamoDb.Models;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;

namespace Amaury.Store.DynamoDb
{
    public class DynamoDbCelebrityEventStore : ICelebrityEventStore
    {
        private readonly IDynamoDBContext _dbContext;
        private readonly ICelebrityEventFactory _eventFactory;
        private readonly DynamoDBOperationConfig _configuration;
        private readonly DynamoEventStoreOptions _options;

        public DynamoDbCelebrityEventStore(IAmazonDynamoDB client, ICelebrityEventFactory eventFactory, DynamoEventStoreOptions options)
        {
            if(client is null) throw new ArgumentNullException(nameof(client));
            _eventFactory = eventFactory ?? throw new ArgumentNullException(nameof(eventFactory));

            _options = options;
            _configuration = new DynamoDBOperationConfig
            {
                OverrideTableName = options.EventStore,
                Conversion = DynamoDBEntryConversion.V2,
                IndexName = options.IndexName
            };

            _dbContext = new DynamoDBContext(client);
        }

        public async Task CommitBatchAsync(IEnumerable<CelebrityEventBase> events, CancellationToken cancellationToken = default)
        {
            if(events is null) throw new ArgumentNullException(nameof(events));

            var celebrityEvents = events.ToList();
            if(celebrityEvents.Any() is false) return;

            var table = _dbContext.GetTargetTable<DynamoDbEventModel>(_configuration);
            var writer = table.CreateBatchWrite();

            var eventModels = ParseToDynamoDbEventModels(celebrityEvents);
            foreach(var document in eventModels.Select(@event => _dbContext.ToDocument(@event))) { writer.AddDocumentToPut(document); }

            await writer.ExecuteAsync(cancellationToken);
        }

        public async Task CommitAsync(CelebrityEventBase @event, CancellationToken cancellationToken = default)
        {
            if(@event is null) throw new ArgumentNullException(nameof(@event));

            var table = _dbContext.GetTargetTable<DynamoDbEventModel>(_configuration);
            var document = _dbContext.ToDocument(@event);

            var data = @event.AggregateVersion == 0 ? null : await table.GetItemAsync(@event.AggregateId, @event.AggregateVersion - 1, cancellationToken);

            await table.PutItemAsync(document, new PutItemOperationConfig { Expected = data }, cancellationToken);
        }

        public async Task<IEnumerable<CelebrityEventBase>> ReadEventsAsync(string aggregateId, long? version = null, bool consistentRead = false, CancellationToken cancellationToken = default)
        {
            if(aggregateId is null) throw new ArgumentNullException(nameof(aggregateId));

            var table = _dbContext.GetTargetTable<DynamoDbEventModel>(_configuration);

            var search = table.Query(new QueryOperationConfig
            {
                KeyExpression = new Expression
                {
                    ExpressionStatement = "#aggregate_id = :v_aggregate_id and #aggregate_version >= :v_aggregate_version",
                    ExpressionAttributeNames = new Dictionary<string, string>
                    {
                        { "#aggregate_id", "AggregateId" },
                        { "#aggregate_version", "AggregateVersion" }
                    },
                    ExpressionAttributeValues = new Dictionary<string, DynamoDBEntry>
                    {
                        { ":v_aggregate_id", aggregateId },
                        { ":v_aggregate_version", version ?? 1 }
                    },
                },
                IndexName = _options.IndexName,
                ConsistentRead = consistentRead
            });

            var events = new List<DynamoDbEventModel>();
            do
            {
                var items = await search.GetNextSetAsync(cancellationToken);

                if(items.Any())
                {
                    events.AddRange(_dbContext.FromDocuments<DynamoDbEventModel>(items));
                }
            }
            while(search.IsDone is false);

            return ParseToCelebrityEvents(events);
        }

        private IEnumerable<CelebrityEventBase> ParseToCelebrityEvents(IEnumerable<DynamoDbEventModel> documents)
            => documents.Select(ParseToCelebrityEventBase);

        private CelebrityEventBase ParseToCelebrityEventBase(IEventStoreModel document)
        {
            var @event = _eventFactory.GetEvent(document.Name, document.Data);

            @event.SetAggregateId(document.AggregateId);
            @event.SetAggregateVersion(document.AggregateVersion);
            @event.SetTimestamp(document.Timestamp);

            return @event;
        }

        private IEnumerable<DynamoDbEventModel> ParseToDynamoDbEventModels(IEnumerable<CelebrityEventBase> events) => events.Select(@event => new DynamoDbEventModel(@event));
    }
}