namespace Koan.Data.Connector.Mongo.Infrastructure;

internal static class Constants
{
    internal static class Provider
    {
        internal const string Name = "mongo";
        internal const string Alias = "mongodb";
        internal const string ConfigurationName = "Mongo";
        internal const int Priority = 20;
        internal const int MaximumRoutes = 128;
        internal const int MaximumCollectionsPerRepository = 1024;
        internal const int DuplicateKeyError = 11000;
    }

    internal static class Configuration
    {
        internal const string Auto = "auto";
        internal const string ZenGardenPrefix = "zen-garden://";
        internal const string Section = "Koan:Data:Mongo";
        internal const string ConnectionString = Section + ":ConnectionString";
        internal const string Database = Section + ":Database";
        internal const string Username = Section + ":Username";
        internal const string Password = Section + ":Password";
        internal const string DisableAutoDetection = Section + ":DisableAutoDetection";

        internal const string DefaultSourceConnectionString = "Koan:Data:Sources:Default:mongo:ConnectionString";
        internal const string DefaultSourceDatabase = "Koan:Data:Sources:Default:mongo:Database";
        internal const string DefaultSourceUsername = "Koan:Data:Sources:Default:mongo:Username";
        internal const string DefaultSourcePassword = "Koan:Data:Sources:Default:mongo:Password";
        internal const string StandardConnectionString = "ConnectionStrings:Mongo";
    }

    internal static class Discovery
    {
        internal const string ServiceName = "mongo";
        internal const string WellKnownServiceName = "mongodb";
        internal const string DatabaseParameter = "database";
        internal const string LocalConnectionString = "mongodb://localhost:27017";
        internal const string MongoUrls = "MONGO_URLS";
        internal const string MongoDbUrls = "MONGODB_URLS";
        internal const int DefaultPort = 27017;
    }

    internal static class Storage
    {
        internal const string Identity = "_id";
        internal const string ManagedDocument = "__koan_document";
        internal const string SetOnInsert = "$setOnInsert";
    }

    internal static class Index
    {
        internal const string Name = "name";
        internal const string Key = "key";
        internal const string Unique = "unique";
        internal const string Sparse = "sparse";
        internal const string Hidden = "hidden";
        internal const string PartialFilter = "partialFilterExpression";
        internal const string Collation = "collation";
        internal const string Expiry = "expireAfterSeconds";
        internal const string Options = "options";
    }

    internal static class StorageStatus
    {
        internal const string Ready = "ready";
        internal const string Timeout = "mongo-timeout";
        internal const string Unavailable = "mongo-unavailable";
    }
}
