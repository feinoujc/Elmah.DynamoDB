# Elmah.DynamoDB

Log Elmah Errors to DynamoDB


## Installation

NOTE: manual configuration required
```ps
Install-Package Elmah.DynamoDB
```

## Configuration

### AWS Configuration

Standard AWS SDK Configuration is required before using the elmah dynamoDB driver. The configuration guide can be found here: http://docs.aws.amazon.com/AWSSdkDocsNET/V3/DeveloperGuide/net-dg-config-creds.html

the IAM Identity will need the following permissions

* GetItem
* PutItem
* Query
* CreateTable
* UpdateTable

### Minimal Configuration
in your web.config, set the error log type

```
<elmah>
    <errorLog type="Elmah.DynamoDB.DynamoDBErrorLog, Elmah.DynamoDB" applicationName="MyApplication" />
</elmah>
```

This will log errors to a table named "Elmah_ErrorLog"
### Configuration Options
```
<elmah>
    <errorLog type="Elmah.DynamoDB.DynamoDBErrorLog, Elmah.DynamoDB" applicationName="MyApplication" 
      tableName="MyTable"
      awsProfileName="elmah"
      awsRegion="us-west-1"
      streamEnabled="false"
      readCapacityUnits="50"
      writeCapacityUnits="10"
    />
</elmah>
```
* **tableName** = the name of the DynamoDB table (default: Elmah_ErrorLog)
* **awsProfileName** = The name of the custom AWS [profile](https://blogs.aws.amazon.com/net/post/Tx1310VG2O81PSY/Referencing-Credentials-using-Profiles) to use for credentials. You can use this to give the Elmah driver a different IAM identity than the rest of your application
* **awsRegion** = the name of the AWS region to use (Only used if awsProfileName is set)
* **streamEnabled** = enable DynamoDB [streams](http://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Streams.html) (default: true)
* **readCapacityUnits** = the read capacity units to use when first creating this table (default: 8)
* **writeCapacityUnits** = the write capacity units to use when first creating this table (default: 6)

 







