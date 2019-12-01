using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.DocumentModel;
using Amazon.DynamoDBv2.Model;
using Amazon.Runtime.CredentialManagement;

namespace Elmah.DynamoDB
{
    public class DynamoDBErrorLog : ErrorLog, IDisposable
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
            : this(GetClient(configuration), GetApplicationName(configuration))
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
        }

        public bool StreamEnabled { get; set; } = true;

        public string TableName { get; set; } = "Elmah_Error";

        public int WriteCapacityUnits { get; set; } = 6;

        public int ReadCapacityUnits { get; set; } = 8;

        public override string Name => "Amazon DynamoDB Error Log";

        public override ErrorLogEntry GetError(string id)
        {
            if (id == null) throw new ArgumentNullException(nameof(id));

            AssertTableExists();

            if (!Guid.TryParse(id, out var key))
            {
                throw new ArgumentException($"Invalid id '{id}'", nameof(id));
            }

            using (var context = new DynamoDBContext(_client))
            {
                var entity = context.Load<ErrorEntity>(key, new DynamoDBOperationConfig { OverrideTableName = TableName });
                var error = ErrorXml.DecodeString(entity.AllXml);
                return new ErrorLogEntry(this, id, error);
            }
        }

        public override IAsyncResult BeginGetError(string id, AsyncCallback asyncCallback, object asyncState)
        {
            return GetErrorAsync()
                .ContinueWith(t => asyncCallback(t));

            async Task<ErrorLogEntry> GetErrorAsync()
            {
                if (id == null) throw new ArgumentNullException(nameof(id));

                AssertTableExists();

                if (!Guid.TryParse(id, out var key))
                {
                    throw new ArgumentException($"Invalid id '{id}'", nameof(id));
                }

                using (var context = new DynamoDBContext(_client))
                {
                    var entity = await context
                        .LoadAsync<ErrorEntity>(key, new DynamoDBOperationConfig { OverrideTableName = TableName })
                        .ConfigureAwait(false);
                    var error = ErrorXml.DecodeString(entity.AllXml);
                    return new ErrorLogEntry(this, id, error);
                }
            }
        }

        public override ErrorLogEntry EndGetError(IAsyncResult asyncResult)
        {
            return ((Task<ErrorLogEntry>)asyncResult).Result;
        }


        public override int GetErrors(int pageIndex, int pageSize, IList errorEntryList)
        {
            AssertTableExists();

            var max = pageSize * (pageIndex + 1);

            Dictionary<string, AttributeValue> lastEvaluatedKey = null;
            var errors = new List<ErrorLogEntry>(max);

            // have to start at the beginning and go through up to the current page, this means it will perform worse as we go through more pages
            // usually, we are just looking at the first few pages so ¯\_(ツ)_/¯

            // low level scanning http://docs.aws.amazon.com/amazondynamodb/latest/developerguide/LowLevelDotNetScanning.html#LowLevelDotNetScanningOptions
            do
            {
                var request = new QueryRequest(TableName)
                {
                    KeyConditionExpression = "Application = :v_appl",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        {":v_appl", new AttributeValue(ApplicationName)}
                    },
                    IndexName = "Application-TimeUtc-index",
                    ScanIndexForward = false,
                    Limit = max,
                    Select = Select.ALL_PROJECTED_ATTRIBUTES
                };
                if (lastEvaluatedKey != null)
                {
                    request.ExclusiveStartKey = lastEvaluatedKey;
                }

                var response = _client.Query(request);
                errors.AddRange(from item in response.Items
                                let errorXml = item["AllXml"].S
                                let errorId = item["ErrorId"].S
                                let error = ErrorXml.DecodeString(errorXml)
                                select new ErrorLogEntry(this, errorId, error));

                lastEvaluatedKey = response.LastEvaluatedKey;
            } while (lastEvaluatedKey != null && lastEvaluatedKey.Count > 0 && errors.Count < max);

            var numberToSkip = pageIndex * pageSize;
            errors = errors.Skip(numberToSkip).Take(pageSize).ToList();
            errors.ForEach(err => errorEntryList.Add(err));

            // get total count of items in the table. 
            // This value is stale (updates every six hours) but will do the job in most cases

            // the other alternative would be to do another scan of the entire index with a Select.COUNT

            var total = _client.DescribeTable(new DescribeTableRequest(TableName)).Table.ItemCount;

            return Convert.ToInt32(Math.Max(errorEntryList.Count, total));
        }


        // async used when downloading all the logs, use TPL, wrap as APM
        // https://msdn.microsoft.com/en-us/library/dd997423(v=vs.110).aspx#Anchor_2

        public override IAsyncResult BeginGetErrors(int pageIndex, int pageSize, IList errorEntryList,
            AsyncCallback asyncCallback,
            object asyncState)
        {
            return GetErrorsAsync(pageIndex, pageSize, errorEntryList)
                .ContinueWith(t => asyncCallback(t));
        }

        public override int EndGetErrors(IAsyncResult asyncResult)
        {
            return ((Task<int>)asyncResult).Result;
        }

        /// <summary>
        /// async clone of <see cref="GetErrors"/>
        /// </summary>
        private async Task<int> GetErrorsAsync(int pageIndex, int pageSize, IList errorEntryList)
        {
            var max = pageSize * (pageIndex + 1);

            Dictionary<string, AttributeValue> lastEvaluatedKey = null;
            var errors = new List<ErrorLogEntry>(max);

            do
            {
                var request = new QueryRequest(TableName)
                {
                    KeyConditionExpression = "Application = :v_appl",
                    ExpressionAttributeValues = new Dictionary<string, AttributeValue>
                    {
                        {":v_appl", new AttributeValue(ApplicationName)}
                    },
                    IndexName = "Application-TimeUtc-index",
                    ScanIndexForward = false,
                    Limit = max,
                    Select = Select.ALL_PROJECTED_ATTRIBUTES
                };
                if (lastEvaluatedKey != null)
                {
                    request.ExclusiveStartKey = lastEvaluatedKey;
                }

                var response = await _client.QueryAsync(request).ConfigureAwait(false);
                errors.AddRange(from item in response.Items
                                let errorXml = item["AllXml"].S
                                let errorId = item["ErrorId"].S
                                let error = ErrorXml.DecodeString(errorXml)
                                select new ErrorLogEntry(this, errorId, error));

                lastEvaluatedKey = response.LastEvaluatedKey;
            } while (lastEvaluatedKey != null && lastEvaluatedKey.Count > 0 && errors.Count < max);

            var numberToSkip = pageIndex * pageSize;
            errors = errors.Skip(numberToSkip).Take(pageSize).ToList();
            errors.ForEach(err => errorEntryList.Add(err));

            var total = (await _client.DescribeTableAsync(new DescribeTableRequest(TableName)).ConfigureAwait(false)).Table.ItemCount;

            return Convert.ToInt32(Math.Max(errorEntryList.Count, total));
        }

        public override string Log(Error error)
        {
            AssertTableExists();

            ErrorEntity entity = BuildEntity(error);

            using (var context = new DynamoDBContext(_client))
            {
                context.Save(entity, new DynamoDBOperationConfig { OverrideTableName = TableName });
            }

            return entity.ErrorId.ToString();
        }

        public override IAsyncResult BeginLog(Error error, AsyncCallback asyncCallback, object asyncState)
        {
            return LogErrorAsync()
                .ContinueWith(t => asyncCallback(t));

            async Task<string> LogErrorAsync()
            {
                AssertTableExists();

                ErrorEntity entity = BuildEntity(error);

                using (var context = new DynamoDBContext(_client))
                {
                    await context
                        .SaveAsync(entity, new DynamoDBOperationConfig { OverrideTableName = TableName })
                        .ConfigureAwait(false);
                }

                return entity.ErrorId.ToString();
            }
        }

        public override string EndLog(IAsyncResult asyncResult)
        {
            return ((Task<string>)asyncResult).Result;
        }


        private ErrorEntity BuildEntity(Error error)
        {

            var allXml = ErrorXml.EncodeString(error);
            return new ErrorEntity
            {
                ErrorId = Guid.NewGuid(),
                Application = ApplicationName,
                Host = error.HostName,
                Type = error.Type,
                Source = error.Source,
                Message = error.Message,
                User = error.User,
                StatusCode = error.StatusCode,
                TimeUtc = error.Time.ToUniversalTime(),
                AllXml = allXml
            };
        }

        private void AssertTableExists()
        {
            if (!_tableExists)
            {
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

            //try to wait for it to be created (up to 2 minutes)
            if (result.TableDescription.TableStatus != TableStatus.ACTIVE)
            {
                for (var i = 0; i < 60; i++)
                {
                    try
                    {
                        var describe = _client.DescribeTable(new DescribeTableRequest(TableName));
                        if (describe.Table.TableStatus == TableStatus.CREATING)
                        {
                            Thread.Sleep(TimeSpan.FromSeconds(2));
                            continue;
                        }
                        break;
                    }
                    catch (ResourceNotFoundException)
                    {
                        Thread.Sleep(TimeSpan.FromSeconds(2));
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


        private static AmazonDynamoDBClient GetClient(IDictionary configuration)
        {
            if (configuration == null) throw new ArgumentNullException(nameof(configuration));
            if (configuration.Contains("awsProfileName"))
            {
                var profile = configuration["awsProfileName"].ToString();
                var sharedFile = new SharedCredentialsFile();
                if (!sharedFile.TryGetProfile(profile, out var basicProfile))
                {
                    throw new Exception($"Could get profile '{profile}' from shared credential file {sharedFile.FilePath}");

                }

                if (!AWSCredentialsFactory.TryGetAWSCredentials(basicProfile, sharedFile, out var awsCredentials))
                {
                    throw new Exception($"Could not obtain AWS Credentials from profile '{profile}'");
                }
                return new AmazonDynamoDBClient(awsCredentials, basicProfile.Region);
            }
            return new AmazonDynamoDBClient();
        }

        public void Dispose()
        {
            _client?.Dispose();
        }
    }
}