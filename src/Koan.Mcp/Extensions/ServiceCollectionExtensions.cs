using System;
using Koan.Mcp.CodeMode.Execution;
using Koan.Mcp.CodeMode.Json; // Added for AddCodeModeJson extension
using Koan.Mcp.CodeExecution;
using Koan.Mcp.CodeMode.Sdk;
using Koan.Mcp.Diagnostics;
using Koan.Mcp.Execution;
using Koan.Mcp.Hosting;
using Koan.Mcp.Infrastructure;
using Koan.Mcp.Options;
using Koan.Mcp.Schema;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Koan.Mcp.Initialization;

internal static class McpServiceRegistration
{
    internal static IServiceCollection AddMcpServices(this IServiceCollection services)
    {
        if (services is null) throw new ArgumentNullException(nameof(services));

        services.AddOptions<McpServerOptions>()
            .BindConfiguration(ConfigurationConstants.Section)
            .Validate<IConfiguration>((_, configuration) =>
                    configuration[ConfigurationConstants.FullKey(ConfigurationConstants.Keys.RetiredHttpSseTransport)] is null,
                "Koan:Mcp:EnableHttpSseTransport is retired. Remove it and set EnableStreamableHttpTransport; set EnableLegacySseTransport explicitly if legacy clients still require it.")
            .ValidateOnStart();

        services.TryAddSingleton<SchemaBuilder>();
        services.TryAddSingleton<DescriptorMapper>();
        services.TryAddSingleton<TimeProvider>(_ => TimeProvider.System);
        services.TryAddSingleton<McpEntityRegistry>();
        services.TryAddSingleton<Koan.Mcp.CustomTools.McpCustomToolRegistry>();
        services.TryAddSingleton<Koan.Mcp.CustomTools.McpCustomToolInvoker>();
        services.TryAddSingleton<RequestTranslator>();
        services.TryAddSingleton<ResponseTranslator>();
        services.TryAddSingleton<EndpointToolExecutor>();
        // WEB-0072: the per-caller surface projection (backs the Explorer console + {baseRoute}/map.json).
        services.TryAddSingleton<Koan.Mcp.Resources.McpSurfaceProjector>();
        // WEB-0072 D5: the privileged, un-redacted access map (the governance god-view).
        services.TryAddSingleton<Koan.Mcp.Resources.AccessMapProjector>();
        services.TryAddSingleton<IMcpTransportDispatcher, StreamJsonRpcTransportDispatcher>();
        services.TryAddSingleton<McpServer>();
        services.AddHostedService<StdioTransport>();

        // P1.2: introspection resources. The framework ships koan://entities; apps (and AN8's koan://self)
        // add more via TryAddEnumerable(ServiceDescriptor.Singleton<IMcpResourceProvider, …>()).
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Koan.Mcp.Resources.IMcpResourceProvider, Koan.Mcp.Resources.EntityCatalogResourceProvider>());
        // AN8: koan://self self-introduction (prose + structured), per grant.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Koan.Mcp.Resources.IMcpResourceProvider, Koan.Mcp.Resources.SelfResourceProvider>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Koan.Mcp.Resources.IMcpResourceProvider, Koan.Mcp.Resources.RuntimeFactsResourceProvider>());

        // AI-0037 — the shared, transport-agnostic JSON-RPC dispatch core (every HTTP surface routes through it).
        services.TryAddSingleton<McpRpcDispatcher>();
        // AI-0037 — the unified MCP HTTP session registry + the two transports that ride it: the modern Streamable
        // HTTP transport and the deprecated legacy /sse+/rpc shim (Ph3b collapsed both onto ONE session model).
        services.TryAddSingleton<McpSessionManager>();
        services.AddHostedService(sp => sp.GetRequiredService<McpSessionManager>());
        services.TryAddSingleton<StreamableHttpTransport>();
        services.TryAddSingleton<HttpSseTransport>();
        services.TryAddSingleton<IMcpCapabilityReporter, McpCapabilityReporter>();

        services.AddCors();

        // Code mode services
        // JSON facade (Newtonsoft-backed) for code-mode dynamic operations
        services.AddCodeModeJson();
        services.AddOptions<CodeModeOptions>().BindConfiguration(ConfigurationConstants.CodeMode.Section);
        services.AddOptions<SandboxOptions>().BindConfiguration(ConfigurationConstants.CodeMode.Sandbox.Section);
        services.AddOptions<TypeScriptSdkOptions>().BindConfiguration(ConfigurationConstants.CodeMode.TypeScript.Section);
        services.TryAddSingleton<Koan.Mcp.CodeExecution.ICodeExecutor, JintCodeExecutor>();
        services.TryAddSingleton<TypeScriptSdkGenerator>();
        services.TryAddSingleton<TypeScriptSdkProvider>();
        services.TryAddSingleton<ITypeScriptSdkProvider>(sp => sp.GetRequiredService<TypeScriptSdkProvider>());
        services.TryAddScoped<KoanSdkBindings>();
        // SEC-0004 Phase 3.3: per-scope holder for the code-mode caller principal (set at the execution-scope
        // boundary by JintCodeExecutor, read by the SDK entity proxy).
        services.TryAddScoped<McpCallContext>();

        return services;
    }
}
