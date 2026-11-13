using Microsoft.Extensions.DependencyInjection;
using TCast.Infrastructure.Apk;
using TCast.Infrastructure.Diagnostics;
using TCast.Infrastructure.Http;
using TCast.Infrastructure.Connection;
using TCast.Infrastructure.Discovery;

namespace TCast.Infrastructure.DependencyInjection;

/// <summary>
/// 基础设施层服务注册入口。Desktop 只调用此方法，不直接 new 具体实现。
/// </summary>
public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>注册网络、文件与诊断服务。</summary>
    /// <param name="services">服务集合。</param>
    public static IServiceCollection AddTcastInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDiscoveryService, DiscoveryService>();
        services.AddSingleton<ITcastConnection, TcastConnection>();
        services.AddSingleton<IConnectionCoordinator, ConnectionCoordinator>();

        // 诊断单例同时以具体类型暴露，供诊断页读取快照与导出。
        services.AddSingleton<DiagnosticsService>();
        services.AddSingleton<IDiagnosticsService>(provider => provider.GetRequiredService<DiagnosticsService>());
        services.AddSingleton<IApkSnapshotFactory>(_ => new ApkSnapshotFactory());
        services.AddSingleton<IApkContainerValidator, ApkContainerValidator>();
        // 纯托管解析清单与资源表，不依赖随包 aapt2。
        services.AddSingleton<IApkMetadataReader>(provider =>
            new AxmlApkMetadataReader(provider.GetRequiredService<IApkContainerValidator>()));
        services.AddSingleton<IApkHttpServer, KestrelApkHttpServer>();
        services.AddSingleton<IApkTransferService>(provider => new ApkTransferService(
            provider.GetRequiredService<IConnectionCoordinator>(),
            provider.GetRequiredService<IApkHttpServer>(),
            diagnostics: provider.GetRequiredService<IDiagnosticsService>()));
        return services;
    }
}