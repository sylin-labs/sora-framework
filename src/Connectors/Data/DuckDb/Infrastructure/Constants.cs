namespace Koan.Data.Connector.DuckDb.Infrastructure;

public static class Constants
{
    public const string Provider = "duckdb";
    public const string DefaultSource = "Default";
    public const string DefaultConnection = "Data Source=.koan/data/Koan.duckdb";
    public const int MaximumMemorySources = 256;
    public const int MaximumPlans = 512;
    public const int MaximumBatchItems = 4_096;
    public const int MaximumParameters = 30_000;

    /// <summary>
    /// DuckDB sits one step below SQLite in default-provider election (higher wins): an application
    /// referencing both keeps SQLite as the ambient system of record and addresses DuckDB explicitly —
    /// the analytics complement, not a replacement (DATA-0123).
    /// </summary>
    public const int Priority = 9;

    public static class Configuration
    {
        public const string Section = "Koan:Data:DuckDb";
        public const string DefaultSourceSection = "Koan:Data:Sources:Default:duckdb";

        public static class Keys
        {
            public const string ConnectionString = Section + ":ConnectionString";
            public const string AutoInstallExtensions = Section + ":AutoInstallExtensions";
            public const string EngineAutoInstallExtensions = Section + ":Engine:AutoInstallExtensions";
            public const string ExtensionDirectory = Section + ":ExtensionDirectory";
            public const string EngineExtensionDirectory = Section + ":Engine:ExtensionDirectory";
            public const string DefaultSourceConnectionString = "Koan:Data:Sources:Default:ConnectionString";
            public const string ProviderSourceConnectionString = DefaultSourceSection + ":ConnectionString";
            public const string ConnectionStringsDuckDb = "ConnectionStrings:DuckDb";
            public const string ConnectionStringsDefault = "ConnectionStrings:Default";
            public const string NamingStyle = Section + ":NamingStyle";
            public const string ProviderNamingStyle = DefaultSourceSection + ":NamingStyle";
            public const string Separator = Section + ":Separator";
            public const string ProviderSeparator = DefaultSourceSection + ":Separator";
            public const string DdlPolicy = Section + ":DdlPolicy";
            public const string ProviderDdlPolicy = DefaultSourceSection + ":DdlPolicy";
            public const string SchemaMatching = Section + ":SchemaMatchingMode";
            public const string ProviderSchemaMatching = DefaultSourceSection + ":SchemaMatchingMode";
        }
    }
}
