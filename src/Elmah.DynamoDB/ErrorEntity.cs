using System;
using Amazon.DynamoDBv2.DataModel;

namespace Elmah.DynamoDB
{
    [DynamoDBTable("Elmah_Error")]
    internal class ErrorEntity
    {
        [DynamoDBHashKey]
        public Guid ErrorId { get; set; }

        [DynamoDBGlobalSecondaryIndexHashKey("Application-TimeUtc-index")]
        public string Application { get; set; }

        public string Host { get; set; }
        public string Type { get; set; }
        public string Source { get; set; }
        public string Message { get; set; }
        public string User { get; set; }
        public int StatusCode { get; set; }

        [DynamoDBGlobalSecondaryIndexRangeKey("Application-TimeUtc-index")]
        public DateTime TimeUtc { get; set; }

        public string AllXml { get; set; }
    }
}