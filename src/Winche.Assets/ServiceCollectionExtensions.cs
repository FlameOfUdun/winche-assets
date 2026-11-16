using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Winche.Core;
using Winche.Rules;
using Winche.Rules.DependencyInjection;
using Winche.Rules.Text;
using Winche.Storage.Interfaces;
using Winche.Storage.Locations;

namespace Winche.Assets;

/// <summary>Options for <c>AddWincheAssets</c>.</summary>
public sealed class WincheAssetsOptions
{
    /// <summary>Required. The project's Postgres connection string.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Limits and retention (the starting values when <see cref="UseSettings"/> lets the console change them).</summary>
    public AssetLimits Limits { get; set; } = new();

    internal Func<IServiceProvider, IArchive>? Archive { get; private set; }
    internal List<RuleSet> Rulesets { get; } = [];
    internal bool SettingsEnabled { get; private set; }
    internal bool Stream { get; private set; }

    /// <summary>
    /// Where contents are kept, from code. Without it, the archive Winche.Storage keeps files in (the last IArchive
    /// registered); contents go under <c>winche-assets/</c> there.
    /// </summary>
    public WincheAssetsOptions UseArchive(Func<IServiceProvider, IArchive> archive) { Archive = archive; return this; }

    /// <summary>Lets the console change the limits and move contents to another location (needs AddWincheConfig to save).</summary>
    public WincheAssetsOptions UseSettings() { SettingsEnabled = true; return this; }

    /// <summary>Files are sent by the server instead of through a signed link to the location.</summary>
    public WincheAssetsOptions StreamFiles() { Stream = true; return this; }

    /// <summary>Adds rules (OR-combined); without any, nothing can be read (default deny).</summary>
    public WincheAssetsOptions UseRules(RuleSet rules) { Rulesets.Add(rules); return this; }

    /// <summary>Rules in the rules language, e.g. <c>match translations/{rest=**} { allow read; }</c>.</summary>
    public WincheAssetsOptions UseRulesText(string text)
    {
        try { return UseRules(RuleText.ParseRuleSet(text)); }
        catch (RuleSyntaxException ex) { throw new InvalidOperationException($"Invalid assets rules at line {ex.Line}, column {ex.Column}: {ex.Reason}", ex); }
    }

    public WincheAssetsOptions UseRulesFile(string path)
    {
        var full = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        if (!File.Exists(full)) throw new FileNotFoundException($"Rules file '{full}' was not found.", full);
        return UseRulesText(File.ReadAllText(full));
    }
}

/// <summary>Limits in force (the console's when it may change them) and how files are served.</summary>
public sealed class AssetSettings(SettingsArea<AssetLimits>? area, AssetLimits code, bool stream)
{
    public AssetLimits Limits => area?.Current ?? code;
    public bool StreamFiles => stream;
}

public static class AssetsServiceCollectionExtensions
{
    public const string RulesKey = "winche-assets";
    public const string Product = "assets";

    public static IServiceCollection AddWincheAssets(this IServiceCollection services, Action<WincheAssetsOptions> configure)
    {
        var options = new WincheAssetsOptions();
        configure(options);
        if (string.IsNullOrWhiteSpace(options.ConnectionString)) throw new InvalidOperationException("WincheAssetsOptions.ConnectionString is required.");
        if (options.Limits.Problems() is { Count: > 0 } p) throw new InvalidOperationException($"Assets limits: {string.Join(" ", p)}");

        services.AddNpgsqlDataSource(options.ConnectionString, serviceKey: AssetStore.DataSourceKey);
        services.AddSingleton<AssetStore>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(AssetChanges.Source);
        services.AddWincheRules(RulesKey, o => o.WithMutableRuleSetRepository(RuleSet.Merge(options.Rulesets)));

        SettingsArea<AssetLimits>? limits = null;
        SettingsArea<AssetLocationSettings>? location = null;
        if (options.SettingsEnabled)
        {
            limits = services.AddWincheSettingsArea("assets", "Assets", options.Limits, s => s.Problems());
            location = services.AddWincheSettingsArea(AssetLocationSettings.Area, "Assets location", new AssetLocationSettings(), s => s.Problems());
        }
        services.AddSingleton(new AssetSettings(limits, options.Limits, options.Stream));
        services.AddSingleton(sp =>
        {
            IArchive? code = null;
            var locations = new ArchiveLocations(sp, () => code ??= options.Archive?.Invoke(sp) ?? sp.GetServices<IArchive>().LastOrDefault()
                ?? throw new InvalidOperationException("Assets need somewhere to keep contents: UseArchive(…), or Winche.Storage's archive."));
            return new AssetArchives(location, locations);
        });
        services.AddSingleton<AssetMoves>();
        services.AddSingleton<WincheAssets>();
        services.AddSingleton<AssetSweep>();
        services.AddSingleton(sp => new AssetReader(sp.GetRequiredService<AssetStore>(), sp.GetRequiredService<AssetArchives>(),
            sp.GetRequiredKeyedService<RuleEngine>(RulesKey), () => WincheCaller.Claims, sp.GetRequiredService<AssetSettings>()));
        services.AddSingleton<IWincheProductSetup, AssetsProductSetup>();
        services.AddWincheProductService<AssetSweepService>(Product);
        if (options.SettingsEnabled) services.AddWincheProductService<AssetMoveWorker>(Product);
        return services;
    }
}

/// <summary>Setting the product up: its tables.</summary>
internal sealed class AssetsProductSetup(AssetStore store) : IWincheProductSetup
{
    public string Product => AssetsServiceCollectionExtensions.Product;
    public IReadOnlyList<string> Steps => ["Create the asset tables"];
    public Task RunStepAsync(int step, CancellationToken ct) => store.InitializeAsync(ct);
}
