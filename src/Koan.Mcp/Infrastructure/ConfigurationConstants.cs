namespace Koan.Mcp.Infrastructure;

internal static class ConfigurationConstants
{
    public const string Section = "Koan:Mcp";

    public static class Keys
    {
        public const string EnableStdioTransport = nameof(EnableStdioTransport);
        public const string RetiredHttpSseTransport = "EnableHttpSseTransport";
        public const string EnableStreamableHttpTransport = nameof(EnableStreamableHttpTransport);
        public const string EnableLegacySseTransport = nameof(EnableLegacySseTransport);
        public const string RequireAuthentication = nameof(RequireAuthentication);
        public const string HttpRoute = nameof(HttpRoute);
        public const string PublishCapabilityEndpoint = nameof(PublishCapabilityEndpoint);
        public const string AllowedEntities = nameof(AllowedEntities);
        public const string DeniedEntities = nameof(DeniedEntities);
        public const string Exposure = nameof(Exposure);
    }

    public static class CodeMode
    {
        public const string Section = ConfigurationConstants.Section + ":CodeMode";

        public static class Keys
        {
            public const string Enabled = nameof(Enabled);
            public const string Runtime = nameof(Runtime);
        }

        public static class Sandbox
        {
            public const string Section = CodeMode.Section + ":Sandbox";

            public static class Keys
            {
                public const string CpuMilliseconds = nameof(CpuMilliseconds);
                public const string MemoryMegabytes = nameof(MemoryMegabytes);
                public const string MaxRecursionDepth = nameof(MaxRecursionDepth);
            }
        }

        public static class TypeScript
        {
            public const string Section = CodeMode.Section + ":TypeScript";
        }
    }

    /// <summary>
    /// Builds full configuration path: "Koan:Mcp:{key}".
    /// </summary>
    public static string FullKey(string key) => $"{Section}:{key}";
}
