using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;

namespace Elmah.DynamoDB
{
    public class DynamoDBErrorLog : ErrorLog
    {
        private static readonly object PadLock = new object();
        private static bool _tableExists;

        private readonly AmazonDynamoDBClient _client;

        public DynamoDBErrorLog(AmazonDynamoDBClient client, string applicationName)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (applicationName == null) throw new ArgumentNullException(nameof(applicationName));
            if (string.IsNullOrWhiteSpace(applicationName)) throw new ArgumentException(nameof(applicationName));

            ApplicationName = applicationName;
            _client = client;
        }

        public DynamoDBErrorLog(IDictionary configuration)
            : this(new AmazonDynamoDBClient(), GetApplicationName(configuration))
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            if (configuration.Contains("tableName"))
            {
                TableName = configuration["tableName"].ToString();
            }

            if (configuration.Contains("readCapacityUnits"))
            {
                ReadCapacityUnits = int.Parse(configuration["readCapacityUnits"].ToString());
            }

            if (configuration.Contains("writeCapacityUnits"))
            {
                WriteCapacityUnits = int.Parse(configuration["writeCapacityUnits"].ToString());
            }

            if (configuration.Contains("streamEnabled"))
            {
                StreamEnabled = bool.Parse(configuration["streamEnabled"].ToString());
            }

            if (configuration.Contains("createTable"))
            {
                CreateTable = bool.Parse(configuration["createTable"].ToString());
            }
        }

        public bool StreamEnabled { get; set; } = true;

        public string TableName { get; set; } = "Elmah_Error";

        public int WriteCapacityUnits { get; set; } = 6;

        public int ReadCapacityUnits { get; set; } = 8;

        public override string Name => "Amazon DynamoDB Error Log";
        public bool CreateTable { get; set; } = true;

        public override ErrorLogEntry GetError(string id)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));

            AssertTableExists();

            Guid key;
            if (!Guid.TryParse(id, out key))
            {
                throw new ArgumentException($"'{id}' is not a Guid", nameof(id));
            }

            using (var context = new DynamoDBContext(_client))
            {
                var entity = context.Load<ErrorEntity>(key,
                    new DynamoDBOperationConfig {OverrideTableName = TableName});
                var error = ErrorXml.DecodeString(entity.AllXml);
                return new ErrorLogEntry(this, id, error);
            }
        }

        public override int GetErrors(int pageIndex, int pageSize, IList errorEntryList)
        {
            AssertTableExists();

            var max = pageSize*(pageIndex + 1);

            Dictionary<string, AttributeValue> lastKeyEvaluated = null;
            var list = new List<ErrorLogEntry>(max);
            do
            {
                var request = new QueryRequest(TableName)
                {
                    KeyConditionExpression = "Application = :application",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        {":application", new AttributeValue(ApplicationName)}
                    },
                    IndexName = "Application-TimeUtc-index",
                    ScanIndexForward = false,
                    Limit = max,
                    Select = Select.ALL_PROJECTED_ATTRIBUTES
                };
                if (lastKeyEvaluated != null)
                {
                    request.ExclusiveStartKey = lastKeyEvaluated;
                }

                var response = _client.Query(request);
                foreach (var item in response.Items)
                {
                    var errorXml = item["AllXml"].S;
                    var errorId = item["ErrorId"].S;
                    var error = ErrorXml.DecodeString(errorXml);
                    list.Add(new ErrorLogEntry(this, errorId, error));
                }
                lastKeyEvaluated = response.LastEvaluatedKey;
            } while (lastKeyEvaluated != null && lastKeyEvaluated.Count > 0 && list.Count < max);

            var numToSkip = pageIndex*pageSize;
            list = list.Skip(numToSkip).Take(pageSize).ToList();
            list.ForEach(err => errorEntryList.Add(err));

            // get total count of items in the table. 
            // This value is stale (updates every six hours) but will do the job here
            var total = _client.DescribeTable(new DescribeTableRequest(TableName)).Table.ItemCount;

            return Convert.ToInt32(Math.Max(errorEntryList.Count, total));
        }

        public override string Log(Error error)
        {
            AssertTableExists();

            var errorXml = ErrorXml.EncodeString(error);
            var id = Guid.NewGuid();

            var errorToStore = new ErrorEntity
            {
                ErrorId = id,
                Application = ApplicationName,
                Host = error.HostName,
                Type = error.Type,
                Source = error.Source,
                Message = error.Message,
                User = error.User,
                StatusCode = error.StatusCode,
                TimeUtc = error.Time.ToUniversalTime(),
                AllXml = errorXml
            };

            using (var context = new DynamoDBContext(_client))
            {
                context.Save(errorToStore, new DynamoDBOperationConfig {OverrideTableName = TableName});
            }

            return id.ToString();
        }

        private void AssertTableExists()
        {
            if (!_tableExists)
            {
                if (!CreateTable)
                {
                    throw new ResourceNotFoundException("Could not find table " + TableName);
                }

                lock (PadLock)
                {
                    if (_tableExists)
                    {
                        return;
                    }
                    Table _;
                    _tableExists = Table.TryLoadTable(_client, TableName, out _);
                    if (!_tableExists)
                    {
                        CreateTableImpl();
                        _tableExists = true;
                    }
                }
            }
        }

        private void CreateTableImpl()
        {
            var request = new CreateTableRequest
            {
                TableName = TableName,
                KeySchema = new List<KeySchemaElement>
                {
                    new KeySchemaElement("ErrorId", KeyType.HASH)
                },
                AttributeDefinitions = new List<AttributeDefinition>
                {
                    new AttributeDefinition("Application", ScalarAttributeType.S),
                    new AttributeDefinition("ErrorId", ScalarAttributeType.S),
                    new AttributeDefinition("TimeUtc", ScalarAttributeType.S)
                },
                GlobalSecondaryIndexes = new List<GlobalSecondaryIndex>
                {
                    new GlobalSecondaryIndex
                    {
                        IndexName = "Application-TimeUtc-index",
                        KeySchema = new List<KeySchemaElement>
                        {
                            new KeySchemaElement("Application", KeyType.HASH),
                            new KeySchemaElement("TimeUtc", KeyType.RANGE)
                        },
                        ProvisionedThroughput = new ProvisionedThroughput(ReadCapacityUnits, WriteCapacityUnits),
                        Projection = new Projection {ProjectionType = ProjectionType.ALL}
                    }
                },
                ProvisionedThroughput = new ProvisionedThroughput(ReadCapacityUnits, WriteCapacityUnits),
                StreamSpecification = new StreamSpecification
                {
                    StreamEnabled = StreamEnabled,
                    StreamViewType = StreamViewType.NEW_IMAGE
                }
            };

            var result = _client.CreateTable(request);

            //try to wait for it to be created
            if (result.TableDescription.TableStatus != TableStatus.ACTIVE)
            {
                for (var i = 0; i < 10; i++)
                {
                    try
                    {
                        var describe = _client.DescribeTable(new DescribeTableRequest(TableName));
                        if (describe.Table.TableStatus == TableStatus.CREATING)
                        {
                            Thread.Sleep(TimeSpan.FromSeconds(5));
                            continue;
                        }
                        break;
                    }
                    catch (ResourceNotFoundException)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(5));
                    }
                }
            }


            if (_client.DescribeTable(new DescribeTableRequest(TableName)).Table?.TableStatus != TableStatus.ACTIVE)
            {
                throw new ResourceNotFoundException("Could not create table " + TableName);
            }
        }

        private static string GetApplicationName(IDictionary configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));

            if (!configuration.Contains("applicationName"))
            {
                throw new ArgumentException("missing required 'applicationName' in configuration");
            }
            return configuration["applicationName"].ToString();
        }
    }
}