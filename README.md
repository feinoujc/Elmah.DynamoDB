# Elmah.DynamoDB

Log Elmah Errors to DynamoDB

<a href="http://www.nuget.org/packages/Elmah.DynamoDB/"><img src="https://img.shields.io/nuget/v/Elmah.DynamoDB.svg" title="NuGet Status"></a>

## Installation

NOTE: manual configuration required
```ps
Install-Package Elmah.DynamoDB
```

## Configuration

### AWS Configuration

Standard AWS SDK Configuration is required before using the elmah dynamoDB driver. The configuration guide can be found here: https://docs.aws.amazon.com/sdk-for-net/v3/developer-guide/net-dg-config-creds.html

the IAM Identity will need at least the following permissions

 * dynamodb:DescribeTable
 * dynamodb:GetItem
 * dynamodb:PutItem
 * dynamodb:Query
 * dynamodb:UpdateItem
 * dynamodb:CreateTable (if you want to automatically create the table if not present)


### Minimal Configuration
in your web.config, set the error log type

```xml
<elmah>
    <errorLog type="Elmah.DynamoDB.DynamoDBErrorLog, Elmah.DynamoDB" applicationName="MyApplication" />
</elmah>
```

This will log errors to a table named "Elmah_ErrorLog"
### Configuration Options
```xml
<elmah>
    <errorLog type="Elmah.DynamoDB.DynamoDBErrorLog, Elmah.DynamoDB" applicationName="MyApplication" 
      tableName="MyTable"
      awsProfileName="elmah"
      streamEnabled="false"
      readCapacityUnits="50"
      writeCapacityUnits="10"
    />
</elmah>
```
* **tableName** = the name of the DynamoDB table (default: Elmah_ErrorLog)
* **awsProfileName** = The name of the custom AWS [profile](https://blogs.aws.amazon.com/net/post/Tx1310VG2O81PSY/Referencing-Credentials-using-Profiles) to use for credentials. You can use this to give the Elmah driver a different IAM identity than the rest of your application
* **streamEnabled** = enable DynamoDB [streams](http://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Streams.html) (default: true)
* **readCapacityUnits** = the read capacity units to use when first creating this table (default: 8)
* **writeCapacityUnits** = the write capacity units to use when first creating this table (default: 6)

 







